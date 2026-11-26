using System;
using System.IO;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures;

namespace SubLedger.Ui;

/// <summary>Fonts and images the window uses. Larger text gets its own font so it stays crisp instead of being stretched.</summary>
internal sealed class UiAssets : IDisposable
{
    public UiAssets()
    {
        var atlas = Service.PluginInterface.UiBuilder.FontAtlas;

        // A negative size is a multiple of the user's own Dalamud font size, so the window follows their settings.
        Small = atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(-0.86f)));
        Heading = atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(-1.22f)));
        Title = atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(-1.55f)));
        Figure = atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(-1.85f)));

        var directory = Service.PluginInterface.AssemblyLocation.Directory?.FullName ?? string.Empty;
        Logo = Service.TextureProvider.GetFromFile(Path.Combine(directory, "Assets", "logo.png"));
        LogoSmall = Service.TextureProvider.GetFromFile(Path.Combine(directory, "Assets", "logo-small.png"));
    }

    public IFontHandle Small { get; }
    public IFontHandle Heading { get; }
    public IFontHandle Title { get; }
    public IFontHandle Figure { get; }

    /// <summary>The full logo with the SubLedger name.</summary>
    public ISharedImmediateTexture Logo { get; }

    /// <summary>The round emblem without text, for small sizes.</summary>
    public ISharedImmediateTexture LogoSmall { get; }

    public void Dispose()
    {
        Small.Dispose();
        Heading.Dispose();
        Title.Dispose();
        Figure.Dispose();
    }
}
