using System.Diagnostics;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public sealed class SidebarScriptTests
{
    [Fact(DisplayName = "a project rollback removes a key that did not exist rather than storing the text null")]
    public void RollbackRemovesMissingKey()
    {
        using var temp = new TempDir();
        var helper = System.IO.Path.Combine(temp.Path, "helper.js");
        File.WriteAllText(helper, SidebarStorage.Script);
        var check = System.IO.Path.Combine(temp.Path, "check.js");
        File.WriteAllText(check, """
            const values = new Map([['existing', 'before'], ['unrelated', 'keep']]);
            globalThis.localStorage = {getItem: key => values.get(key) ?? null,
                setItem: (key, value) => values.set(key, String(value)), removeItem: key => values.delete(key)};
            const {projectsOperation} = require(process.argv[2]);
            const keys = ['existing', 'missing'];
            const before = projectsOperation(keys, null);
            projectsOperation(keys, {existing: 'changed', missing: 'created'});
            projectsOperation(keys, before);
            process.stdout.write(JSON.stringify({after: projectsOperation(keys, null),
                unrelated: values.get('unrelated'), missingStored: values.has('missing')}));
            """);
        var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(check);
        start.ArgumentList.Add(helper);
        using var node = Process.Start(start)!;
        var output = node.StandardOutput.ReadToEnd();
        var error = node.StandardError.ReadToEnd();
        node.WaitForExit();
        Assert.True(node.ExitCode == 0, error);
        Assert.Equal("""{"after":{"existing":"before","missing":null},"unrelated":"keep","missingStored":false}""", output);
    }
}
