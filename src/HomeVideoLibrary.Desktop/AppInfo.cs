using System.Reflection;

namespace HomeVideoLibrary.Desktop;

/// <summary>Identity from the desktop assembly, including the SDK's source revision when available.</summary>
public static class AppInfo
{
    private static readonly Assembly Assembly = typeof(AppInfo).Assembly;
    public static string Name => Assembly.GetCustomAttribute<AssemblyProductAttribute>()!.Product;
    public static string Build => Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    public static string Version => Build.Split('+')[0];
}
