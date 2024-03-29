using CommandLine;

namespace redir;

public interface ICmd
{
    [Value(0, Required = true, MetaName = "Target Socket URI")]public string Socket { get; set; }
    [Option('b',"buffer", Default = 1024)]public int BufferSize { get; set; }
    [Option('d',"daemonize", Default = false)]public bool Daemonize { get; set; }
    //[Option('a',"attach", Default = true)]public bool Attach { get; set; }
}

public abstract class ACmd : ICmd
{
    public string Socket { get; set; } = null!;
    public int BufferSize { get; set; }
    public bool Daemonize { get; set; }
    public bool Attach { get; set; }
}

[Verb("start")]
public class StartCmd : ACmd
{
    [Value(1, Required = true, MetaName = "Command to Execute")]public string Command { get; set; } = null!;
}

[Verb("daemon", Hidden = true)]
public class DaemonCmd : StartCmd
{
    [Option("spawn")]public bool Spawn { get; set; }
}

[Verb("attach", isDefault: true)]
public class AttachCmd : ACmd
{
}