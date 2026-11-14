using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace ZenFiles.Services;

/// <summary>
/// Én kjørende ZenFiles per bruker. Starter du programmet på nytt (oppgavelinjemenyen, Win+E som standard
/// filbehandler, dobbeltklikk), sender den nye prosessen en melding til den første og avslutter, og den første
/// åpner et nytt vindu. Da bor alle vinduene i samme prosess, og faner kan flyttes mellom dem.
/// </summary>
public static class SingleInstance
{
    static Mutex? _mutex;
    static string Suffix => Environment.UserName;
    static string PipeName => "ZenFiles.ipc." + Suffix;

    /// <summary>Sann hvis dette er den første instansen.</summary>
    public static bool TryBecomePrimary()
    {
        try
        {
            _mutex = new Mutex(true, @"Local\ZenFiles.Single." + Suffix, out bool created);
            if (!created) { _mutex.Dispose(); _mutex = null; }
            return created;
        }
        catch { return true; }
    }

    public static bool SendToPrimary(string message)
    {
        try
        {
            AllowSetForegroundWindow(-1);   // lar den første instansen hente fram vinduet
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000);
            var bytes = Encoding.UTF8.GetBytes(message + "\n");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch { return false; }
    }

    /// <summary>Lytter etter meldinger fra nye instanser. onMessage kjører på en bakgrunnstråd.</summary>
    public static void StartServer(Action<string> onMessage)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var line = await reader.ReadLineAsync();
                    if (!string.IsNullOrEmpty(line)) onMessage(line);
                }
                catch { await Task.Delay(200); }
            }
        });
    }

    public static void BringToFront(IntPtr hwnd)
    {
        try { SetForegroundWindow(hwnd); } catch { }
    }

    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
}

/// <summary>Oppgavelinjemenyen (høyreklikk på ikonet): «Nytt vindu».</summary>
public static class JumpListHelper
{
    static readonly Guid IidObjectArray = new("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9");

    public static void Update()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            var list = (ICustomDestinationList)new CDestinationList();
            var iid = IidObjectArray;
            int hr = list.BeginList(out uint _, ref iid, out object _);
            if (hr < 0) return;

            var tasks = (IObjectCollection)new CEnumerableObjectCollection();
            tasks.AddObject(MakeLink(exe, "--new-window", "Nytt vindu", "Åpner et nytt ZenFiles-vindu"));
            list.AddUserTasks((IObjectArray)tasks);
            list.CommitList();
        }
        catch { }
    }

    static object MakeLink(string exe, string args, string title, string description)
    {
        var link = (IShellLinkW)new CShellLink();
        link.SetPath(exe);
        link.SetArguments(args);
        link.SetDescription(description);
        link.SetIconLocation(exe, 0);

        var store = (IPropertyStore)link;
        var key = new PropertyKey { fmtid = new Guid("F29F85E0-4FF9-11D1-A8F1-00C04FD0EA2D"), pid = 2 };   // PKEY_Title
        var pv = new PropVariant { vt = 31, p = Marshal.StringToCoTaskMemUni(title) };                      // VT_LPWSTR
        store.SetValue(ref key, ref pv);
        store.Commit();
        PropVariantClear(ref pv);
        return link;
    }

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pv);

    // ───────── COM ─────────

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct PropertyKey { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr p;
    }

    [ComImport, Guid("77F10CF0-3DB5-4966-B520-B7C54FD35ED6"), ClassInterface(ClassInterfaceType.None)]
    class CDestinationList { }

    [ComImport, Guid("2D3468C1-36A7-43B6-AC24-D3F02FD9607A"), ClassInterface(ClassInterfaceType.None)]
    class CEnumerableObjectCollection { }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046"), ClassInterface(ClassInterfaceType.None)]
    class CShellLink { }

    [ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IObjectArray
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object obj);
    }

    [ComImport, Guid("5632B1A4-E38A-400A-928A-D4CD63230295"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IObjectCollection
    {
        // IObjectArray
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object obj);
        // IObjectCollection
        void AddObject([MarshalAs(UnmanagedType.Interface)] object obj);
        void AddFromArray([MarshalAs(UnmanagedType.Interface)] IObjectArray array);
        void RemoveObjectAt(uint index);
        void Clear();
    }

    [ComImport, Guid("6332DEBF-87B5-4670-90C0-5E57B408A49E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        [PreserveSig] int BeginList(out uint minSlots, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object removed);
        [PreserveSig] int AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string category, [MarshalAs(UnmanagedType.Interface)] IObjectArray items);
        void AppendKnownCategory(int category);
        [PreserveSig] int AddUserTasks([MarshalAs(UnmanagedType.Interface)] IObjectArray tasks);
        void CommitList();
        void GetRemovedDestinations(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object obj);
        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string appId);
        void AbortList();
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr pfd, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int cch, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pathRel, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
}
