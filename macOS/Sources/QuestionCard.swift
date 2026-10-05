import SwiftUI

// MARK: - Question card
// Claude's AskUserQuestion in the chat: each question with its choices and a field for an answer
// of one's own. One question with one choice is answered by the click (or ⌘1…), like a
// permission dialog; otherwise the choices are collected and sent together.

struct QuestionCard: View {
    let question: AgentQuestion
    @Binding var answers: [AgentQuestion.Answer]
    let busy: Bool
    let submit: ([AgentQuestion.Answer]) -> Void

    var body: some View {
        GroupBox {
            VStack(alignment: .leading, spacing: 16) {
                ForEach(Array(question.items.enumerated()), id: \.offset) { i, item in
                    if answers.indices.contains(i) { itemView(i, item) }
                }
                if !question.answersOnClick {
                    Button("Send Answers") { submit(answers) }
                        .buttonStyle(AnswerButtonStyle(prominent: true))
                        .controlSize(.large)
                        .keyboardShortcut(.return, modifiers: .command)
                        .help("Send Answers  ⌘↩")
                        .disabled(busy || !question.complete(answers))
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(4)
            .disabled(busy)
        } label: {
            Label(question.items.count > 1 ? "Claude has questions" as LocalizedStringKey : "Claude has a question", systemImage: "questionmark.bubble.fill")
                .foregroundStyle(.orange)
        }
    }

    func itemView(_ i: Int, _ item: AgentQuestion.Item) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            if let header = item.header, !header.isEmpty, question.items.count > 1 {
                Text(header).font(.caption.weight(.semibold)).foregroundStyle(.secondary)
            }
            Text(item.question).font(.headline).textSelection(.enabled)
            if item.multiSelect {
                Text("Choose any").font(.caption).foregroundStyle(.secondary)
            }
            VStack(alignment: .leading, spacing: 2) {
                ForEach(Array(item.options.enumerated()), id: \.offset) { j, option in
                    optionRow(i, item, j, option)
                }
            }
            TextField(item.multiSelect ? "Something else (optional)" as LocalizedStringKey : "Something else", text: otherBinding(i, item))
                .textFieldStyle(.roundedBorder)
                .onSubmit {
                    // One question with one choice: typing an answer and pressing Return sends it.
                    if question.answersOnClick, question.complete(answers) { submit(answers) }
                }
        }
    }

    func optionRow(_ i: Int, _ item: AgentQuestion.Item, _ j: Int, _ option: AgentQuestion.Option) -> some View {
        let picked = answers[i].picked.contains(j)
        let symbol = item.multiSelect ? (picked ? "checkmark.square.fill" : "square") : (picked ? "largecircle.fill.circle" : "circle")
        let shortcut: KeyboardShortcut? = question.answersOnClick && j < 9 ? KeyboardShortcut(KeyEquivalent(Character("\(j + 1)")), modifiers: .command) : nil
        return Button { pick(i, item, j) } label: {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Image(systemName: symbol).foregroundStyle(picked ? AnyShapeStyle(.tint) : AnyShapeStyle(.secondary))
                VStack(alignment: .leading, spacing: 1) {
                    Text(option.label)
                    if let d = option.description, !d.isEmpty {
                        Text(d).font(.callout).foregroundStyle(.secondary)
                    }
                }
                Spacer(minLength: 0)
            }
            .padding(.vertical, 5)
            .padding(.horizontal, 8)
            .contentShape(.rect)
            .background(picked ? AnyShapeStyle(.tint.opacity(0.12)) : AnyShapeStyle(.clear), in: .rect(cornerRadius: 8))
        }
        .buttonStyle(.plain)
        .keyboardShortcut(shortcut)
        .help(shortcut == nil ? option.label : "\(option.label)  ⌘\(j + 1)")
        .accessibilityAddTraits(picked ? .isSelected : [])
    }

    func pick(_ i: Int, _ item: AgentQuestion.Item, _ j: Int) {
        var a = answers[i]
        if item.multiSelect {
            if a.picked.contains(j) { a.picked.remove(j) } else { a.picked.insert(j) }
        } else {
            a.picked = [j]
            a.other = ""
        }
        answers[i] = a
        if question.answersOnClick { submit(answers) }
    }

    /// Typing an answer of one's own replaces a single choice.
    func otherBinding(_ i: Int, _ item: AgentQuestion.Item) -> Binding<String> {
        Binding {
            answers.indices.contains(i) ? answers[i].other : ""
        } set: { text in
            guard answers.indices.contains(i) else { return }
            answers[i].other = text
            if !item.multiSelect && !text.isEmpty { answers[i].picked = [] }
        }
    }
}
