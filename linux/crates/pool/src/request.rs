//! What the relay reads off a request to decide where it may go. Bodies are
//! searched as bytes and never parsed: a request is forwarded exactly as it
//! came, and Claude Code writes its JSON compact and in a fixed order.

use crate::{contains, find, rfind};

/// The one request worth moving. `count_tokens` and everything under `/api/`
/// stay with the account that sent them.
pub fn is_inference(method: &str, target: &str) -> bool {
    method == "POST" && target.split('?').next() == Some("/v1/messages")
}

/// Anything a request refers to by id lives in the organization that made it,
/// an uploaded file, a code container, a server-side MCP connection, and
/// another account asked for it answers that it does not exist. Keys are
/// matched with their closing quote and colon, which JSON never escapes, so
/// the same word inside a prompt does not count.
///
/// Each is matched with the start of the value that makes it a reference, not
/// the key alone. A connector's tool schema may have a property of the same
/// name, `"container":{"type":"object",...}`, and every request that lists its
/// tools carries it: the third live run found every full request pinned to its
/// sender that way, so a conversation moved from the dropdown went home each
/// time Claude Code sent it whole. A tool call naming one of these may still
/// pin a request it need not, which costs only sharing.
pub const ACCOUNT_BOUND_MARKS: [&str; 4] =
    [r#""file_id":""#, r#""container":""#, r#""container":{"id":""#, r#""mcp_servers":[{"#];

pub fn must_stay_with_sender(body: &[u8], content_encoding: Option<&str>) -> bool {
    if let Some(encoding) = content_encoding {
        if !encoding.is_empty() && !encoding.eq_ignore_ascii_case("identity") {
            return true;
        }
    }
    ACCOUNT_BOUND_MARKS.iter().any(|mark| contains(body, mark.as_bytes()))
}

/// Reasoning the model wrote earlier in a conversation is carried back in
/// every later request, and for some models the service honours it only from
/// the account that wrote it: from any other it is dropped without a word, the
/// answer still a 200. A request with none of it loses nothing anywhere, a new
/// conversation or one just compacted, so only one that carries some is held.
/// Matched with quotes and colon like the keys above, so the same words quoted
/// in a prompt do not count.
pub const REASONING_KEYS: [&str; 2] = [r#""type":"thinking""#, r#""type":"redacted_thinking""#];

/// The models whose reasoning was measured surviving a move between accounts
/// in different organizations. Any other is taken to be bound, since holding
/// one that was not costs a little sharing and moving one that was costs its
/// reasoning on every move.
pub const REASONING_TRAVELS: [&str; 1] = ["claude-opus-5-5"];

pub fn carries_bound_reasoning(body: &[u8]) -> bool {
    if !REASONING_KEYS.iter().any(|key| contains(body, key.as_bytes())) {
        return false;
    }
    match model(body) {
        None => true,
        Some(model) => !REASONING_TRAVELS.iter().any(|m| model.starts_with(m)),
    }
}

/// Claude Code keeps a conversation as a thread on the service and, once one
/// exists, sends only the turns since the message it names: the rest is stored
/// with the account that answered it. Any other account answers
/// `thread_not_found`, and Claude Code then sends the whole conversation again
/// as a new thread. Seen with Claude Code 2.1.317, where every moved
/// continuation came back 404 from the account it was moved to.
/// `JSON.stringify` writes `type` first, as the object is built.
pub const THREAD_CONTINUATION: &str = r#""thread":{"type":"continue""#;
pub const THREAD_KEY: &str = r#""thread":{"#;

pub fn continues_thread(body: &[u8]) -> bool {
    contains(body, THREAD_CONTINUATION.as_bytes())
}

/// A conversation's first message carries no answer yet. Every later request,
/// and every short one Claude Code asks beside an existing conversation,
/// carries the turns so far.
pub fn starts_conversation(body: &[u8]) -> bool {
    !contains(body, br#""role":"assistant""#)
}

/// A turn a person typed: one on the conversation's thread whose last user
/// message holds no tool's answer. Claude Code carries on after every tool
/// with a user message that is nothing but `tool_result`s, and the short
/// requests it makes beside a conversation carry no thread; a lone message
/// with no thread is as likely a title asked for as a first message, and a
/// first message is marked as begun anyway. A message typed while a tool was
/// running rides along with that tool's answer and is missed; the next one
/// typed is not.
pub fn starts_turn(body: &[u8]) -> bool {
    if !carries_thread(body) {
        return false;
    }
    let marker = br#""role":"user""#;
    let Some(last) = rfind(body, marker) else { return false };
    !contains(&body[last + marker.len()..], br#""type":"tool_result""#)
}

/// The conversation's own turns carry a thread; the short requests Claude Code
/// makes beside them, a title or a summary, share its session id and carry
/// none.
pub fn carries_thread(body: &[u8]) -> bool {
    contains(body, THREAD_KEY.as_bytes())
}

/// The service's word for a thread it does not hold, which Claude Code answers
/// by starting the thread again with everything in it.
pub const THREAD_MISSING: &[u8] = b"thread_not_found";

/// For the record of what went where. The first `"model":` is the request's
/// own, Claude Code writing it ahead of the messages; a wrong guess here
/// mislabels one line on screen and decides nothing.
pub fn model(body: &[u8]) -> Option<String> {
    let key = br#""model":""#;
    let start = find(body, key)? + key.len();
    let window = &body[start..body.len().min(start + 120)];
    let end = window.iter().position(|b| *b == b'"')?;
    Some(String::from_utf8_lossy(&window[..end]).into_owned())
}

pub fn bearer(headers: &[(String, String)]) -> Option<String> {
    let value = headers.iter().find(|(n, _)| n.eq_ignore_ascii_case("authorization"))?.1.as_str();
    if value.len() <= 7 || !value.is_char_boundary(7) || !value[..7].eq_ignore_ascii_case("bearer ") {
        return None;
    }
    Some(value[7..].trim().to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_message_to_the_model_is_the_request_worth_moving() {
        assert!(is_inference("POST", "/v1/messages?beta=true"));
        assert!(!is_inference("POST", "/v1/messages/count_tokens?beta=true"), "counting tokens is not");
        assert!(!is_inference("GET", "/v1/messages"), "nor is anything but a POST");
        assert!(!is_inference("POST", "/api/oauth/profile"), "nor anything under the account's own API");
    }

    const UPLOAD: &[u8] =
        br#"{"model":"claude-x","messages":[{"content":[{"type":"document","source":{"type":"file","file_id":"file_1"}}]}]}"#;

    #[test]
    fn a_request_naming_an_uploaded_file_stays_with_the_account_holding_the_file() {
        assert!(must_stay_with_sender(UPLOAD, None));
        let quoted = br#"{"messages":[{"content":"what is \"file_id\": for"}]}"#;
        assert!(!must_stay_with_sender(quoted, None), "the same key quoted inside a prompt pins nothing");
        assert!(must_stay_with_sender(quoted, Some("gzip")), "a compressed body cannot be read, so it is not moved");
        assert!(!must_stay_with_sender(quoted, Some("identity")), "an uncompressed one can be");
    }

    #[test]
    fn a_tool_whose_schema_has_a_property_of_the_same_name_pins_nothing() {
        let schema = br#"{"tools":[{"input_schema":{"properties":{"container":{"type":"object"},"file_id":{"type":"string"},"mcp_servers":{"type":"array"}}}}]}"#;
        assert!(!must_stay_with_sender(schema, None));
        for marker in [&br#""container":"c1""#[..], br#""container":{"id":"c1"}"#, br#""mcp_servers":[{"type":"url"}]"#] {
            assert!(must_stay_with_sender(marker, None), "while a reference by value does");
        }
    }

    const REASONED: &str = r#"{"model":"claude-sonnet-5-5","messages":[{"role":"assistant","content":[{"type":"thinking","thinking":"...","signature":"x"}]}]}"#;

    #[test]
    fn a_conversation_carrying_reasoning_back_is_held_to_the_account_that_wrote_it() {
        assert!(carries_bound_reasoning(REASONED.as_bytes()));
        assert!(
            carries_bound_reasoning(br#"{"model":"claude-haiku-5-5","messages":[{"content":[{"type":"redacted_thinking","data":"x"}]}]}"#),
            "and so is one carrying reasoning the service wrote out of sight"
        );
        assert!(
            !carries_bound_reasoning(br#"{"model":"claude-sonnet-5-5","thinking":{"type":"adaptive"},"messages":[{"role":"user","content":"hi"}]}"#),
            "a new conversation asking to reason has none yet to lose"
        );
        assert!(
            !carries_bound_reasoning(br#"{"model":"claude-sonnet-5-5","messages":[{"content":"what does \"type\":\"thinking\" mean"}]}"#),
            "the words quoted inside a prompt hold nothing"
        );
    }

    #[test]
    fn a_model_whose_reasoning_was_seen_surviving_the_move_is_routed_as_before() {
        assert!(!carries_bound_reasoning(REASONED.replace("claude-sonnet-5-5", "claude-opus-5-5[1m]").as_bytes()));
        assert!(
            carries_bound_reasoning(REASONED.replace("claude-sonnet-5-5", "claude-next-1").as_bytes()),
            "while one nobody has measured is taken to be bound"
        );
    }

    #[test]
    fn the_model_and_the_bearer_are_read_off_the_request() {
        assert_eq!(model(UPLOAD).as_deref(), Some("claude-x"));
        let h = |n: &str, v: &str| vec![(n.to_string(), v.to_string())];
        assert_eq!(bearer(&h("authorization", "Bearer abc ")).as_deref(), Some("abc"), "whatever the field's case");
        assert_eq!(bearer(&h("Authorization", "Basic abc")), None, "and only a bearer counts");
    }

    #[test]
    fn a_continued_thread_is_known_by_its_type_written_first() {
        assert!(continues_thread(br#"{"thread":{"type":"continue","previous_message_id":"m1"}}"#));
        assert!(!continues_thread(br#"{"thread":{"type":"create"}}"#));
        assert!(carries_thread(br#"{"thread":{"type":"create"}}"#));
    }

    #[test]
    fn a_turn_typed_on_a_thread_is_a_person_typing() {
        let typed = br#"{"model":"m","messages":[{"role":"user","content":[{"type":"text","text":"Is 391 prime?"}]}],"thread":{"type":"create"}}"#;
        let lone = br#"{"model":"m","messages":[{"role":"user","content":"Name this conversation: Is 391 prime?"}]}"#;
        let tool_answer = br#"{"model":"m","thread":{"id":"t"},"messages":[{"role":"user","content":"Is 391 prime?"},{"role":"assistant","content":[{"type":"tool_use","id":"u"}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"u","content":"17 x 23"}]}]}"#;
        let next_turn = br#"{"model":"m","thread":{"id":"t"},"messages":[{"role":"user","content":[{"type":"tool_result","tool_use_id":"u","content":"17 x 23"}]},{"role":"assistant","content":"No: 17 x 23."},{"role":"user","content":"And 397?"}]}"#;
        let aside = br#"{"model":"m","messages":[{"role":"user","content":"Is 391 prime?"},{"role":"assistant","content":"No."},{"role":"user","content":"Name this conversation."}]}"#;
        assert!(starts_turn(typed) && starts_turn(next_turn), "a first message and a turn typed on its thread");
        assert!(!starts_turn(tool_answer), "Claude Code carrying on with a tool's answer is not");
        assert!(!starts_turn(aside), "nor a short request made beside a conversation, which carries no thread");
        assert!(!starts_turn(lone), "nor a lone message with no thread, as likely a title as a first message");
    }

    #[test]
    fn a_conversations_first_message_carries_no_answer_yet() {
        assert!(starts_conversation(br#"{"messages":[{"role":"user","content":"a"}]}"#));
        assert!(!starts_conversation(br#"{"messages":[{"role":"user","content":"a"},{"role":"assistant","content":"b"}]}"#));
    }
}
