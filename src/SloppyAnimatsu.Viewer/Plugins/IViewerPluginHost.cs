using Avalonia.Controls;
using SloppyAnimatsu.Core.Plugins;

namespace SloppyAnimatsu.Viewer.Plugins;

/// <summary>The viewer's plugin host: <see cref="IPluginHost"/> plus the window plugins open their own windows over.</summary>
public interface IViewerPluginHost : IPluginHost
{
    Window Owner { get; }
}
