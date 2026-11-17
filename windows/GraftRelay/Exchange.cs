using System.Text;
using System.Text.Json;
using ClaudeGraft.Core;

namespace ClaudeGraft.Relay;

/// <summary>
/// One TLS connection from Claude Code and every request down it. A model
/// request is read whole first, since it may go out more than once; an answer
/// is never held, which is what keeps a streamed reply streaming.
/// </summary>
public sealed class Exchange(Wire client, PoolState state, Upstream upstream)
{
    private const int HeadLimit = 64 * 1024;
    /// Far beyond any conversation with a few images in it.
    private const long BodyLimit = 256L * 1024 * 1024;

    private Wire? _connection;
    /// The error type of the last refusal read and found not to be a missing
    /// thread, for the diagnostics line that follows it.
    private string? _refusal;

    public async Task Run()
    {
        try { while (await ServeOne()) { } }
        catch (Exception e) when (e is IOException or ObjectDisposedException or System.Net.Sockets.SocketException or FormatException or OperationCanceledException) { }
        _connection?.Close();
        client.Close();
    }

    private void Drop()
    {
        _connection?.Close();
        _connection = null;
    }

    private Task Fail(int status, string reason, string message) =>
        client.Send(HttpMessage.Failure(status, reason, message)).ContinueWith(_ => { });

    /// False when the connection should close after this request.
    private async Task<bool> ServeOne()
    {
        if (await client.ReadHead(HeadLimit) is not var (head, raw)) return false;
        if (head.Line.Count != 3 || head.Method == "CONNECT" || head.RequestBody is not { } framing)
        {
            await Fail(400, "Bad Request", "The relay could not read that request.");
            return false;
        }
        state.Began();
        try
        {
            if (head.Tokens("expect").Contains("100-continue"))
                await client.Send("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray());

            var own = PoolRequest.Bearer(head.Fields) is string bearer ? state.MemberFor(bearer) : null;
            // Only a model request from a Claude the pool knows can change
            // accounts; rebuilding anything else would drop Upgrade, which is
            // all it takes to stop a socket opening.
            if (!PoolRequest.IsInference(head.Method, head.Target) || own is null || head.AsksToUpgrade)
                return await PassThrough(head, raw, framing);

            byte[] body;
            try { body = await client.ReadBody(framing, BodyLimit); }
            catch (Wire.TooLarge)
            {
                await Fail(413, "Payload Too Large", "That request is too large for the relay.");
                return false;
            }
            return await Route(head, body, own);
        }
        finally { state.Ended(); }
    }

    private async Task<bool> Route(HttpHead head, byte[] body, string own)
    {
        var session = head.Value("x-claude-code-session-id");
        var pinned = PoolRequest.MustStayWithSender(body, head.Value("content-encoding"));
        var continuing = PoolRequest.ContinuesThread(body);
        var attempts = state.Plan(own, session, pinned, continuing || PoolRequest.CarriesBoundReasoning(body));
        var model = PoolRequest.Model(body);
        var cache = PoolPolicy.CacheLifetime(body);

        var passed = new List<PoolPass>();
        var index = 0;
        while (index < attempts.Count)
        {
            var attempt = attempts[index];
            var last = index == attempts.Count - 1;
            var borrowed = attempt.Token is not null;
            // A moved thread's refusal is read to tell thread_not_found from a
            // plan that lacks something, and a compressed one cannot be.
            var request = HttpMessage.Request(head, body, attempt.Token is string t ? "Bearer " + t : null, continuing && borrowed);
            (HttpHead Head, byte[] Raw) answer;
            try { answer = await Send(request); }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
            {
                Drop();
                if (last)
                {
                    await Fail(502, "Bad Gateway", $"The relay could not reach {UsagePool.Host}: {e.Message}");
                    return false;
                }
                if (attempt.Member is string m) passed.Add(new PoolPass(m, PoolPassReason.Unreachable));
                index++;
                continue;
            }

            var status = answer.Head.Status ?? 502;
            if (attempt.Member is string member)
            {
                state.Observe(member, answer.Head.Fields);
                var outcome = PoolPolicy.OutcomeOf(status, answer.Head.Fields, borrowed, DateTimeOffset.UtcNow);
                if (outcome is PoolPolicy.Outcome.RefusedForAccount && status == 404 && continuing && !last
                    && await ThreadMissing(answer, head.Method) is { } missing)
                {
                    // Sending it home would undo the move; Claude Code answers
                    // this by starting the thread again here.
                    Diagnostics.Note("relay.thread-restarted", new Dictionary<string, object?> { ["member"] = member });
                    state.Served(member, own, session, status, passed, model, cache, threaded: true,
                        starts: PoolRequest.StartsConversation(body), prompted: PoolRequest.StartsTurn(body));
                    return await Deliver(answer, missing, head.Method, head.KeepsAlive);
                }
                if (outcome is not PoolPolicy.Outcome.Deliver && !last)
                {
                    state.Apply(outcome, member);
                    // A few hundred bytes nobody will read; dropping the
                    // connection is cheaper than draining it.
                    Drop();
                    switch (outcome)
                    {
                        case PoolPolicy.Outcome.Exhausted:
                            passed.Add(new PoolPass(member, PoolPassReason.Full));
                            index++;
                            break;
                        case PoolPolicy.Outcome.LoginRefused:
                            passed.Add(new PoolPass(member, PoolPassReason.Login));
                            index++;
                            break;
                        default:
                            // What one plan lacks the next may lack too; the
                            // sender's next attempt is the one sure to take it.
                            passed.Add(new PoolPass(member, PoolPassReason.Refused));
                            var home = attempts.FindIndex(index + 1, a => a.Token is null);
                            index = home >= 0 ? home : attempts.Count - 1;
                            break;
                    }
                    var noted = new Dictionary<string, object?> { ["member"] = member, ["status"] = status, ["reason"] = passed[^1].Reason.ToString() };
                    if (continuing) noted["thread"] = "continue";
                    if (_refusal is not null) noted["error"] = _refusal;
                    _refusal = null;
                    Diagnostics.Note("relay.passed-over", noted);
                    continue;
                }
                state.Served(member, own, session, status, passed, model, cache,
                    threaded: PoolRequest.CarriesThread(body), starts: PoolRequest.StartsConversation(body),
                    prompted: PoolRequest.StartsTurn(body));
            }
            return await Deliver(answer, head.Method, head.KeepsAlive);
        }
        return false;
    }

    /// Goes as it arrived, under its sender's login. A stated length is read
    /// first so a kept connection can be retried; a chunked body may still be
    /// coming when its answer starts, and an upgrade carries no more HTTP, so
    /// those get a connection of their own and their bodies follow alongside.
    private async Task<bool> PassThrough(HttpHead head, byte[] raw, HttpBody framing)
    {
        var upgrade = head.AsksToUpgrade;
        (HttpHead Head, byte[] Raw) answer;
        Task? upload = null;
        if (!upgrade && framing is not HttpBody.Chunked)
        {
            byte[] body;
            try { body = await client.ReadBody(framing, BodyLimit); }
            catch (Wire.TooLarge)
            {
                await Fail(413, "Payload Too Large", "That request is too large for the relay.");
                return false;
            }
            try { answer = await Send([.. raw, .. body]); }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
            {
                Drop();
                await Fail(502, "Bad Gateway", $"The relay could not reach {UsagePool.Host}: {e.Message}");
                return false;
            }
        }
        else
        {
            Drop();
            Wire fresh;
            try { fresh = await upstream.Connect(); }
            catch (Exception e)
            {
                await Fail(502, "Bad Gateway", $"The relay could not reach {UsagePool.Host}: {e.Message}");
                return false;
            }
            _connection = fresh;
            await fresh.Send(raw);
            upload = Forward(framing, fresh);
            answer = await FinalHead(fresh) ?? throw new RelayException("closed before answering");
        }

        if (upgrade && answer.Head.Status == 101 && _connection is { } up)
        {
            if (upload is not null) await upload;
            await client.Send(answer.Raw);
            _connection = null;
            await Wire.Splice(client, up);
            return false;
        }
        var more = await Deliver(answer, head.Method, head.KeepsAlive);
        if (upload is not null) await upload;
        return more;
    }

    /// The rest of a request's body, as it comes, down the connection its head went.
    private async Task Forward(HttpBody framing, Wire up)
    {
        switch (framing)
        {
            case HttpBody.Length l:
                var remaining = l.Count;
                while (remaining > 0)
                {
                    if (client.Buffered == 0 && !await client.Fill()) throw new RelayException("truncated body");
                    var piece = client.Take((int)Math.Min(remaining, client.Buffered));
                    remaining -= piece.Length;
                    await up.Send(piece);
                }
                break;
            case HttpBody.Chunked:
                var chunked = new ChunkedFraming();
                while (true)
                {
                    if (client.Buffered > 0) await up.Send(client.Take(chunked.Consume(client.Buffer, null)));
                    if (chunked.IsDone) return;
                    if (!await client.Fill()) throw new RelayException("truncated body");
                }
        }
    }

    /// A kept connection the far end closed while idle dies before a byte of
    /// answer; that one is tried again fresh, and nothing that failed later is.
    private async Task<(HttpHead Head, byte[] Raw)> Send(byte[] request)
    {
        if (_connection is { } reused)
        {
            var before = reused.Received;
            try
            {
                await reused.Send(request);
                if (await FinalHead(reused) is { } answer) return answer;
            }
            catch (Exception e) when (reused.Received == before && e is IOException or ObjectDisposedException or System.Net.Sockets.SocketException) { }
            if (reused.Received != before) throw new RelayException("connection died mid-answer");
            Drop();
        }
        var fresh = await upstream.Connect();
        _connection = fresh;
        await fresh.Send(request);
        return await FinalHead(fresh) ?? throw new RelayException("closed before answering");
    }

    /// Skips interim answers; the relay said 100 Continue itself.
    private static async Task<(HttpHead Head, byte[] Raw)?> FinalHead(Wire wire)
    {
        while (true)
        {
            if (await wire.ReadHead(HeadLimit) is not { } answer) return null;
            if (answer.Head.Status is int s && s >= 100 && s < 200 && s != 101) continue;
            return answer;
        }
    }

    /// The body of a refusal when it is the service saying it holds no such
    /// thread: which error it is decides whether it goes back or on.
    private async Task<byte[]?> ThreadMissing((HttpHead Head, byte[] Raw) answer, string method)
    {
        if (_connection is not { } up) return null;
        var framing = answer.Head.ResponseBody(method);
        if (framing is HttpBody.UntilClose) return null;
        if (answer.Head.Value("content-encoding") is string encoding && encoding.Length > 0
            && !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)) return null;
        var body = await up.ReadBody(framing, 64 * 1024);
        if (body.AsSpan().IndexOf(PoolRequest.ThreadMissing) >= 0) return body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            _refusal = doc.RootElement.GetProperty("error").GetProperty("type").GetString();
        }
        catch { _refusal = "unreadable"; }
        return null;
    }

    /// An answer whose body was read to decide on it; a chunked one goes back
    /// as a single chunk, the same body however it was cut.
    private async Task<bool> Deliver((HttpHead Head, byte[] Raw) answer, byte[] body, string method, bool keepAlive)
    {
        await client.Send(answer.Raw);
        if (answer.Head.ResponseBody(method) is HttpBody.Chunked)
        {
            if (body.Length > 0)
                await client.Send((byte[])[.. Encoding.ASCII.GetBytes(body.Length.ToString("x") + "\r\n"), .. body, .. "\r\n"u8.ToArray()]);
            await client.Send("0\r\n\r\n"u8.ToArray());
        }
        else await client.Send(body);
        if (!answer.Head.KeepsAlive || _connection?.Buffered > 0) Drop();
        return keepAlive && answer.Head.KeepsAlive;
    }

    private async Task<bool> Deliver((HttpHead Head, byte[] Raw) answer, string method, bool keepAlive)
    {
        if (_connection is not { } up) return false;
        await client.Send(answer.Raw);
        switch (answer.Head.ResponseBody(method))
        {
            case HttpBody.Length l:
                var remaining = l.Count;
                while (remaining > 0)
                {
                    if (up.Buffered == 0 && !await up.Fill()) throw new RelayException("truncated answer");
                    var piece = up.Take((int)Math.Min(remaining, up.Buffered));
                    remaining -= piece.Length;
                    await client.Send(piece);
                }
                break;
            case HttpBody.Chunked:
                var chunked = new ChunkedFraming();
                while (true)
                {
                    if (up.Buffered > 0) await client.Send(up.Take(chunked.Consume(up.Buffer, null)));
                    if (chunked.IsDone) break;
                    if (!await up.Fill()) throw new RelayException("truncated answer");
                }
                break;
            case HttpBody.UntilClose:
                await client.Send(up.TakeAll());
                while (await up.Fill()) await client.Send(up.TakeAll());
                Drop();
                return false;
        }
        if (!answer.Head.KeepsAlive || up.Buffered > 0) Drop();
        // The service's Connection: close went back with the head, so Claude
        // Code is closing its end too.
        return keepAlive && answer.Head.KeepsAlive;
    }
}
