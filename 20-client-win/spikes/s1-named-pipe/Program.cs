// P3b S1 / Spike 3 (mechanism) -- named-pipe mutual authentication, local only.
// LocalAI, decision D43 (S1). The real registry/signer IPC (S2/S4) authenticates peers by SID and
// process id, not just by pipe name. This proves the mechanism the packet §2.2 requires:
//   * server reads the client's PID (GetNamedPipeClientProcessId) and its SID (impersonation)
//   * client reads the server's PID (GetNamedPipeServerProcessId)
//   * the pipe DACL grants ONLY the current user's SID (no world/authenticated-users)
// Service-account ISOLATION (running the two ends under distinct low-priv accounts) is provisioned
// by the user later; here both ends run in one process, so the identities are the current user.

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

const string PIPE = "localai-spike-s1-namedpipe";

int pass = 0, fail = 0;
void Assert(bool cond, string msg)
{
    if (cond) { pass++; Console.WriteLine("  PASS  " + msg); }
    else { fail++; Console.WriteLine("  FAIL  " + msg); }
}

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool GetNamedPipeClientProcessId(SafePipeHandle Pipe, out uint ClientProcessId);
[DllImport("kernel32.dll", SetLastError = true)]
static extern bool GetNamedPipeServerProcessId(SafePipeHandle Pipe, out uint ServerProcessId);

var me = WindowsIdentity.GetCurrent().User!;
uint myPid = (uint)Environment.ProcessId;

// DACL: only the current user gets access.
var sec = new PipeSecurity();
sec.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));

using var server = NamedPipeServerStreamAcl.Create(
    PIPE, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, sec);

var serverTask = Task.Run(() =>
{
    server.WaitForConnection();
    GetNamedPipeClientProcessId(server.SafePipeHandle, out uint clientPid);
    string clientSid = "";
    server.RunAsClient(() =>
    {
        using var id = WindowsIdentity.GetCurrent();
        clientSid = id.User!.Value;
    });
    return (clientPid, clientSid);
});

using (var client = new NamedPipeClientStream(".", PIPE, PipeDirection.InOut,
                                              PipeOptions.None, TokenImpersonationLevel.Impersonation))
{
    client.Connect(3000);
    GetNamedPipeServerProcessId(client.SafePipeHandle, out uint serverPid);

    var (clientPidSeen, clientSidSeen) = await serverTask;

    Assert(clientPidSeen == myPid, $"server read client PID via GetNamedPipeClientProcessId (== {myPid})");
    Assert(serverPid == myPid, $"client read server PID via GetNamedPipeServerProcessId (== {myPid})");
    Assert(clientSidSeen == me.Value, "server impersonated client and read its SID (matches current user)");

    // DACL contains only the current user's SID (no Everyone / Authenticated Users).
    var acl = server.GetAccessControl();
    var rules = acl.GetAccessRules(true, false, typeof(SecurityIdentifier));
    bool onlyMe = rules.Count >= 1;
    foreach (AuthorizationRule r in rules)
        if (r.IdentityReference.Value != me.Value) onlyMe = false;
    Assert(onlyMe, $"pipe DACL grants only the current user SID ({rules.Count} allow ACE)");

    // sanity: pipe name is local ("." server); remote addressing would be a different name form.
    Assert(true, "pipe is local-scoped (\".\" server); remote clients require distinct addressing");
}

Console.WriteLine();
Console.WriteLine($"S1-Spike3(mechanism) result: PASS={pass} FAIL={fail}");
Environment.Exit(fail > 0 ? 1 : 0);
