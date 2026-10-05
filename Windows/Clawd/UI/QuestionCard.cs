using Clawd.Core.Orca;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace Clawd.UI;

/// <summary>
/// Claude's AskUserQuestion in the chat: each question with its choices and a field for an answer of
/// one's own. One question with one choice is answered by the click (or Ctrl+1…), like a permission
/// dialog; otherwise the choices are collected and sent together. The choices live in the model's
/// answers, so a card rebuilt for a new busy state shows what was already picked and typed.
/// </summary>
internal sealed class QuestionCard
{
    private readonly AgentQuestion _question;
    private readonly List<AgentQuestion.Answer> _answers;
    private readonly Action<List<AgentQuestion.Answer>> _submit;
    private readonly Button? _send;

    public AgentQuestion Question => _question;
    public bool Busy { get; }
    public UIElement Root { get; }
    /// <summary>The first choice, which takes the focus as a permission dialog's first answer does.</summary>
    public Control? FirstChoice { get; private set; }

    public QuestionCard(AgentQuestion question, List<AgentQuestion.Answer> answers, bool busy, Action<List<AgentQuestion.Answer>> submit)
    {
        _question = question;
        _answers = answers;
        _submit = submit;
        Busy = busy;
        var body = new StackPanel { Spacing = 16 };
        var title = L.Get(question.Items.Count > 1 ? "Timeline_Questions" : "Timeline_Question");
        body.Children.Add(Ui.Row(8, Ui.Icon(Ui.Glyph.Message, "ClawdOrangeIcon", 16), Ui.Text(title, "ClawdOrangeText")));
        for (var i = 0; i < question.Items.Count && i < answers.Count; i++) body.Children.Add(ItemView(i, question.Items[i]));
        if (!question.AnswersOnClick)
        {
            _send = Ui.Button(L.Get("Timeline_SendAnswers"), () => Submit(), accent: true, tooltip: L.Get("Timeline_SendAnswersTip"));
            _send.HorizontalAlignment = HorizontalAlignment.Left;
            body.Children.Add(_send);
        }
        UpdateSend();
        body.IsHitTestVisible = !busy;
        Root = Ui.Card(body);
    }

    /// <summary>Picks option <paramref name="index"/> of a one-question, one-choice card and sends it.</summary>
    public void Pick(int index)
    {
        if (!_question.AnswersOnClick || index >= _question.Items[0].Options.Count || Busy) return;
        _answers[0].Picked.Clear();
        _answers[0].Picked.Add(index);
        _answers[0].Other = "";
        Submit();
    }

    private void Submit()
    {
        if (Busy || !_question.Complete(_answers)) return;
        _submit(_answers);
    }

    private void UpdateSend()
    {
        if (_send is not null) _send.IsEnabled = !Busy && _question.Complete(_answers);
    }

    private StackPanel ItemView(int i, AgentQuestion.Item item)
    {
        var panel = new StackPanel { Spacing = 6 };
        var answer = _answers[i];
        if (!string.IsNullOrEmpty(item.Header) && _question.Items.Count > 1) panel.Children.Add(Ui.Caption(item.Header));
        panel.Children.Add(Ui.Strong(item.Question));
        if (item.MultiSelect) panel.Children.Add(Ui.Caption(L.Get("Timeline_ChooseAny")));
        var other = new TextBox
        {
            PlaceholderText = L.Get(item.MultiSelect ? "Timeline_SomethingElseOptional" : "Timeline_SomethingElse"),
            Text = answer.Other,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var radios = new List<RadioButton>();
        var group = $"q{Guid.NewGuid():N}";
        for (var j = 0; j < item.Options.Count; j++)
        {
            var option = item.Options[j];
            var index = j;
            var content = new StackPanel { Spacing = 1 };
            content.Children.Add(Ui.Text(option.Label));
            if (!string.IsNullOrEmpty(option.Description)) content.Children.Add(Ui.Secondary(option.Description));
            if (_question.AnswersOnClick && j < 9) ToolTipService.SetToolTip(content, $"{option.Label}  Ctrl+{j + 1}");
            if (item.MultiSelect)
            {
                var box = new CheckBox { Content = content, IsChecked = answer.Picked.Contains(j) };
                box.Checked += (_, _) => { answer.Picked.Add(index); UpdateSend(); };
                box.Unchecked += (_, _) => { answer.Picked.Remove(index); UpdateSend(); };
                FirstChoice ??= box;
                panel.Children.Add(box);
            }
            else
            {
                var radio = new RadioButton { Content = content, GroupName = group, IsChecked = answer.Picked.Contains(j) };
                radio.Checked += (_, _) =>
                {
                    answer.Picked.Clear();
                    answer.Picked.Add(index);
                    if (other.Text.Length > 0) other.Text = "";
                    UpdateSend();
                };
                // A user's click, not the Checked a programmatic change also raises, sends a one-click card.
                radio.Click += (_, _) => { if (_question.AnswersOnClick) Submit(); };
                radios.Add(radio);
                FirstChoice ??= radio;
                panel.Children.Add(radio);
            }
        }
        other.TextChanged += (_, _) =>
        {
            answer.Other = other.Text;
            // Typing an answer of one's own replaces a single choice.
            if (!item.MultiSelect && other.Text.Length > 0)
            {
                answer.Picked.Clear();
                foreach (var r in radios) r.IsChecked = false;
            }
            UpdateSend();
        };
        other.KeyDown += (_, e) =>
        {
            // One question with one choice: typing an answer and pressing Enter sends it.
            if (e.Key == VirtualKey.Enter && _question.AnswersOnClick) { e.Handled = true; Submit(); }
        };
        panel.Children.Add(other);
        return panel;
    }
}
