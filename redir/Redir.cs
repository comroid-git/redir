using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using CommandLine;
using comroid.common;

namespace redir;

public static class Redir
{
    public static readonly Log log = new("redir");
    public static readonly Regex SocketUriPattern = new("((?<scheme>tcp|udp):\\/\\/)?(?<ip>(\\d{1,3}\\.){3}\\d{1,3}|localhost|\\*)(:(?<port>\\d+))?|(unix:(?<path>(\\/[\\w.]+)+))");
    private static Encoding Encoding = Encoding.ASCII;

    static Redir()
    {
        ILog.Detail = DetailLevel.None;
    }

    public static void Main(params string[] args)
    {
        new Parser(cfg =>
            {
                cfg.CaseSensitive = false;
                cfg.CaseInsensitiveEnumValues = true;
                cfg.HelpWriter = Console.Out;
                cfg.IgnoreUnknownArguments = true;
                cfg.AutoHelp = true;
                cfg.AutoVersion = true;
                cfg.ParsingCulture = CultureInfo.InvariantCulture;
                cfg.EnableDashDash = false;
                cfg.MaximumDisplayWidth = log.RunWithExceptionLogger(() => Console.WindowWidth, "Could not get Console Width", _ => 1024, LogLevel.Debug);
            }).ParseArguments<DaemonCmd, StartCmd, AttachCmd>(args)
            .WithParsed(Run<DaemonCmd>(Daemon))
            .WithParsed(Run<StartCmd>(Start))
            .WithParsed(Run<AttachCmd>(Attach))
            .WithNotParsed(Error);
    }

    #region Command Methods
    
    private static void Daemon(DaemonCmd cmd, Socket? socket, EndPoint? endPoint)
    {
        if (cmd.Spawn)
        {
            Process.Start("redir", $"daemon \"{cmd.Socket}\" \"{cmd.Command}\"");
            log.Info("Daemon process starting");
            return;
        }
        
        Start(cmd, socket!, endPoint!);
    }

    private static void Attach(AttachCmd cmd, Socket socket, EndPoint endPoint)
    {
        socket.Connect(endPoint);

        if (!socket.Connected)
        {
            log.Error("Unable to connect to socket");
            return;
        }

        void RedirectInput()
        {
            while (socket.Connected)
            {
                var buf = Console.ReadLine()!;
                socket.Send(Encoding.GetBytes(buf));
            }
        }

        void RedirectOutput()
        {
            while (socket.Connected)
            {
                var buf = new byte[cmd.BufferSize];
                var read = socket.Receive(buf);
                Console.WriteLine(Encoding.GetString(buf[..read]));
            }
        }

        new Thread(() => log.RunWithExceptionLogger(RedirectOutput)).Start();
        RedirectInput();
    }

    private static void Start(StartCmd cmd, Socket socket, EndPoint endPoint)
    {
        var command = cmd.Command.Split(" ");
        string exe = command[0], arg = string.Join(" ", command[1..]);
        var start = new ProcessStartInfo(exe, arg)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        socket.Bind(endPoint);
        socket.Listen(1);
        
        var proc = Process.Start(start)!;
        while (!proc.HasExited)
        {
            socket = socket.Accept();
            log.Debug("Connected");

            if (!socket.IsBound)
            {
                log.Error("Unable to bind socket");
                return;
            }

            ThreadStart RedirectInput(Socket socket, StreamWriter input)
            {
                return () => log.RunWithExceptionLogger(() =>
                {
                    while (socket.IsBound)
                    {
                        var buf = new byte[cmd.BufferSize];
                        var read = socket.Receive(buf);
                        var str = Encoding.GetString(buf[..read]);
                        log.Debug("[Input] " + str);
                        input.WriteLine(str);
                        input.Flush();
                    }
                });
            }

            ThreadStart RedirectOutput(Socket socket, StreamReader output)
            {
                return () => log.RunWithExceptionLogger(() =>
                {
                    while (socket.IsBound)
                    {
                        var buf = output.ReadLine()!;
                        log.Debug("[Output] " + buf);
                        var readOnlySpan = Encoding.GetBytes(buf);
                        socket.Send(readOnlySpan);
                    }
                });
            }

            new Thread(RedirectInput(socket, proc.StandardInput)).Start();
            new Thread(RedirectOutput(socket, proc.StandardOutput)).Start();
            new Thread(RedirectOutput(socket, proc.StandardError)).Start();

            while (socket.Connected)
                Thread.Sleep(500);
        }
        
        socket.Dispose();
        log.Info("Process finished");
    }

    #endregion


    #region Utility Methods

    private static void Error(IEnumerable<Error> errors)
    {
        foreach (var error in errors)
            log.At(LogLevel.Error, error);
    }

    private static Socket NamedSocket(string objSocket)
    {
        throw new NotImplementedException();
    }

    private static Action<CMD> Run<CMD>(Action<CMD, Socket, EndPoint> handler) where CMD : ICmd
    {
        return cmd =>
        {
            if (cmd is DaemonCmd { Spawn: true })
                handler(cmd, null!, null!);
            
            Socket socket;
            EndPoint endPoint;
            if (SocketUriPattern.Match(cmd.Socket) is { Success: true } match)
            {
                if (match.Groups["path"].Value is { Length: > 0 } path)
                {
                    // unix socket
                    if (!path.StartsWith("/"))
                        path = Path.Combine(Environment.CurrentDirectory, path);
                    
                    socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.IP);
                    endPoint = new UnixDomainSocketEndPoint(path);
                }
                else
                {
                    // tcp or udp socket
                    var ip = IPAddress.Parse(match.Groups["ip"].Value);
                    var port = match.Groups["port"].Value is { Length: > 0 } portStr
                        ? int.Parse(portStr)
                        : throw new ArgumentException("No port Specified");
                    endPoint = new IPEndPoint(ip, port);

                    var scheme = match.Groups["scheme"].Value;
                    if (string.IsNullOrEmpty(scheme))
                        scheme = "tcp";
                    switch (scheme)
                    {
                        case "tcp":
                            socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                            break;
                        case "udp":
                            socket = new Socket(endPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                            break;
                        default:
                            log.Error("Unknown Scheme: " + scheme);
                            return;
                    }
                }
            }
            //else { sock = NamedSocket(cmd.Socket); }
            else
            {
                log.Error("Invalid Socket URI: " + cmd.Socket);
                return;
            }

            try
            {
                handler(cmd, socket, endPoint);
            }
            finally
            {
            }
        };
    }

    #endregion
}
