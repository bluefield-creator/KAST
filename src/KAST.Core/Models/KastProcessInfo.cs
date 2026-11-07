namespace KAST.Core.Models;

/// <summary>
/// An Arma process on this host that carries a KAST instance tag
/// (<c>-name=server_{InstanceId}</c> or <c>-name=server_{InstanceId}_hc{n}</c>).
/// Used to hunt down processes the panel has lost track of.
/// </summary>
public sealed record KastProcessInfo(
    int Pid,
    int InstanceId,
    int? HeadlessClientIndex,
    string? ExecutablePath,
    string? CommandLine)
{
    public bool IsHeadlessClient => HeadlessClientIndex.HasValue;
}
