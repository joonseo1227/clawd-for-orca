using Clawd.Core;
using Clawd.Core.Orca;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Clawd.Services;

/// <summary>
/// When Clawd is hidden its cards can't be seen, so agents that need you (and finished work)
/// arrive as Windows notifications instead, answerable in place: type a reply in the toast and it
/// goes to the agent's terminal. Focus assist and the per-app notification settings apply as usual.
/// A downloaded update is announced the same way.
/// </summary>
internal sealed class Notifier : IDisposable
{
    public enum Action { Reply, Open, Show }   // Show: the toast itself was clicked
    public enum Category { Question, Permission, Done }

    private readonly DispatcherQueue _dispatcher;
    private bool _registered;

    /// <summary>What the user did with a toast: the pane it was about, the action, typed text.
    /// Raised on the UI thread.</summary>
    public event Action<string, Action, string?>? Responded;

    /// <summary>The update toast: true for "Restart to update", false when the toast itself was clicked.
    /// Raised on the UI thread.</summary>
    public event Action<bool>? UpdateResponded;

    /// <summary>Tag and group of the one update toast; pane keys ("tab:leaf") never look like this.</summary>
    private const string UpdateTag = "clawd-update";

    public Notifier(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    public void Start()
    {
        try
        {
            if (!AppNotificationManager.IsSupported()) return;
            // Subscribe before registering, or an activation that launched Clawd could be missed.
            AppNotificationManager.Default.NotificationInvoked += (_, args) => _dispatcher.TryEnqueue(() => Handle(args));
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception e)
        {
            Log.Error($"notifications unavailable: 0x{e.HResult:X8} {e.Message}");
        }
    }

    /// <summary>A toast clicked while Clawd wasn't running started it; act on that click too.</summary>
    public void Handle(AppNotificationActivatedEventArgs args)
    {
        if (args.Arguments.TryGetValue("update", out var update))
        {
            UpdateResponded?.Invoke(update == "install");
            return;
        }
        if (!args.Arguments.TryGetValue("pane", out var pane)) return;
        var action = args.Arguments.TryGetValue("action", out var a) ? a switch
        {
            "reply" => Action.Reply,
            "open" => Action.Open,
            _ => Action.Show,
        } : Action.Show;
        var text = args.UserInput.TryGetValue("reply", out var t) ? t : null;
        Responded?.Invoke(pane, action, text);
    }

    public void Post(Category category, OrcaAgent agent, string title, string? body, bool sound)
    {
        if (!_registered) return;
        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("pane", agent.PaneKey)
                .AddArgument("action", "show")
                .AddText(title)
                .AddText(agent.Name)
                // One toast per agent and state: a newer one replaces the older.
                .SetTag(category.ToString())
                .SetGroup(Group(agent.PaneKey));
            if (!string.IsNullOrEmpty(body)) builder.AddText(body);
            // A permission dialog or Claude's questions are answered by choosing options, which needs
            // the chat.
            if (category != Category.Permission && agent.CanMessage && !agent.AsksQuestion)
            {
                builder.AddTextBox("reply", L.Get("Toast_ReplyPlaceholder"), L.Get("Toast_ReplyTitle"));
                builder.AddButton(new AppNotificationButton(L.Get("Toast_Send"))
                    .AddArgument("pane", agent.PaneKey).AddArgument("action", "reply").SetInputId("reply"));
            }
            builder.AddButton(new AppNotificationButton(L.Get("Toast_OpenInOrca")).AddArgument("pane", agent.PaneKey).AddArgument("action", "open"));
            if (!sound) builder.MuteAudio();
            AppNotificationManager.Default.Show(builder.BuildNotification());
            Log.Debug(() => $"notified {agent.PaneKey}#{category}");
        }
        catch (Exception e)
        {
            Log.Error($"notification failed: {e.Message}");
        }
    }

    /// <summary>A downloaded update is ready. "What's new" opens the release page in the browser
    /// without involving Clawd.</summary>
    public void PostUpdate(string version, Uri page, bool sound)
    {
        if (!_registered) return;
        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("update", "show")
                .AddText(L.Format("Toast_UpdateAvailable", version))
                .AddText(L.Get("Toast_UpdateBody"))
                .SetTag(UpdateTag)
                .SetGroup(UpdateTag)
                .AddButton(new AppNotificationButton(L.Get("Update_Restart")).AddArgument("update", "install"))
                .AddButton(new AppNotificationButton(L.Get("Update_WhatsNew")).SetInvokeUri(page));
            if (!sound) builder.MuteAudio();
            AppNotificationManager.Default.Show(builder.BuildNotification());
            Log.Debug(() => $"notified update {version}");
        }
        catch (Exception e)
        {
            Log.Error($"update notification failed: {e.Message}");
        }
    }

    public void ClearUpdate() => Clear(UpdateTag);

    /// <summary>The agent was dealt with (answered, opened): its toasts are stale.</summary>
    public void Clear(string paneKey)
    {
        if (!_registered) return;
        _ = AppNotificationManager.Default.RemoveByGroupAsync(Group(paneKey)).AsTask().ContinueWith(
            t => Log.Debug(() => $"clear toasts: {t.Exception?.GetBaseException().Message}"), TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>Toast groups are limited to 64 characters; pane keys are well under, but be safe.</summary>
    private static string Group(string paneKey) => paneKey.Length <= 64 ? paneKey : paneKey[..64];

    public void Dispose()
    {
        if (!_registered) return;
        try { AppNotificationManager.Default.Unregister(); }
        catch (Exception e) { Log.Debug(() => $"unregister: {e.Message}"); }
        _registered = false;
    }
}
