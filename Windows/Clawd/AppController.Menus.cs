using Clawd.Core;
using Clawd.Core.Orca;
using Clawd.Core.Pet;
using Clawd.Services;

namespace Clawd;

// The notification-area menu and Clawd's right-click menu.
internal sealed partial class AppController
{
    private NativeMenu TrayMenu()
    {
        var menu = new NativeMenu();
        AddAgentItems(menu);
        menu.AddSeparator();
        menu.Add(L.Get(Hidden ? "Menu_Show" : "Menu_Hide"), ToggleHidden);
        if (!Hidden) menu.Add(L.Get("Menu_Snack"), DropSnack);
        if (UpdateMenuTitle is { } update) menu.Add(update, InstallUpdate);
        menu.Add(L.Get("Menu_Settings"), () => OpenSettings());
        menu.AddSeparator();
        menu.Add(L.Get("Menu_Quit"), Quit);
        return menu;
    }

    private NativeMenu PetMenu()
    {
        var menu = new NativeMenu();
        AddAgentItems(menu);
        menu.AddSeparator();
        menu.Add(L.Get("Menu_Snack"), DropSnack);
        var tricks = new NativeMenu();
        foreach (var a in Activities.All) tricks.Add(a.Title(), () => _pet.Start(a));
        menu.AddSubmenu(L.Get("Menu_Tricks"), tricks);
        menu.Add(L.Get(_pet.Following ? "Menu_StopFollowing" : "Menu_Follow"), _pet.ToggleFollow);
        menu.Add(L.Get(_pet.Current == PetBrain.State.Sleep ? "Menu_Wake" : "Menu_Sleep"), () =>
        {
            if (_pet.Current == PetBrain.State.Sleep) _pet.Poked(); else _pet.Sleep();
        });
        menu.AddSeparator();
        menu.Add(L.Get("Menu_Hide"), ToggleHidden);
        if (UpdateMenuTitle is { } update) menu.Add(update, InstallUpdate);
        menu.Add(L.Get("Menu_Settings"), () => OpenSettings());
        menu.Add(L.Get("Menu_Quit"), Quit);
        return menu;
    }

    /// <summary>Agent rows, each with a submenu to open the terminal or send a message.</summary>
    private void AddAgentItems(NativeMenu menu)
    {
        if (!Orca.Available) { menu.AddHeader(L.Get("Chat_NoOrca")); return; }
        if (!Orca.Enabled) { menu.AddHeader(L.Get("Menu_OrcaOff")); return; }
        var now = DateTimeOffset.Now;

        void Row(string title, OrcaAgent a, string? detail)
        {
            var sub = new NativeMenu();
            if (detail is not null) { sub.AddHeader(detail); sub.AddSeparator(); }
            sub.Add(L.Get("Menu_OpenInOrca"), () => Open(a));
            // A session in Orca's chat takes no messages from Clawd, but the chat still shows it.
            sub.Add(L.Get(!a.CanMessage ? "Menu_ShowInClawd" : a.NeedsYou ? "Menu_ReplyWithClawd" : "Menu_TalkWithClawd"), () => OpenChat(a.PaneKey));
            menu.AddSubmenu(title, sub);
        }

        var waiting = Orca.Waiting.ToList();
        var working = Orca.Working.ToList();
        var done = Attention.Finished.Values.OrderByDescending(f => f.At).ToList();
        if (waiting.Count == 0 && working.Count == 0 && done.Count == 0) menu.AddHeader(L.Get("Menu_AllResting"));
        if (waiting.Count > 0)
        {
            menu.AddHeader(L.Get("Menu_NeedsYou"));
            foreach (var a in waiting)
            {
                var waited = (now - Attention.Since(a, now)).TotalSeconds;
                var title = waited < 60 ? L.Format("Menu_WaitingRowJustNow", a.Name) : L.Format("Menu_WaitingRow", a.Name, Text.Duration(waited));
                Row(title, a, Text.Snippet(a.Ask, 70));
            }
        }
        if (working.Count > 0)
        {
            menu.AddHeader(L.Get("Menu_Working"));
            foreach (var a in working)
            {
                var since = a.StateStartedAt is { } s ? $" · {Text.Duration((now - s).TotalSeconds)}" : "";
                Row(a.Name + (a.Tool is { } t ? $" · {t}" : "") + since, a, Text.Snippet(a.Prompt, 70));
            }
        }
        if (done.Count > 0)
        {
            menu.AddHeader(L.Get("Menu_RecentlyFinished"));
            foreach (var f in done.Take(5)) Row($"{f.Agent.Name} · {Text.Ago(f.At, now)}", f.Agent, Text.Snippet(f.Agent.LastMessage, 70));
        }
        // Shows the global shortcut beside the item, as menus do for their own shortcuts.
        menu.Add(L.Get("Menu_TalkToClawd"), () => Dispatcher.TryEnqueue(() => OpenChat(fromTray: true)), Settings.Shortcut.Display);
    }
}
