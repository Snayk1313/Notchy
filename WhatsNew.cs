using System.Windows.Media;

namespace Notchy;

/// <summary>
/// What's new in this version, shown once on the island after an update (MainWindow.WhatsNew.cs) and from
/// Settings → About. Only versions with notes here show anything: small fixes (2.0.x) pass quietly.
/// Keep it in step with the release notes (the commit message that publishes the version).
/// </summary>
public static class WhatsNew
{
    /// <summary>The version these notes are for (major.minor).</summary>
    public const string Version = "2.0";

    public sealed record Highlight(string Art, Color Color, string TagPt, string TagEn, string TitlePt, string TitleEn, string TextPt, string TextEn)
    {
        public string Tag => Loc.Language == "en" ? TagEn : TagPt;
        public string Title => Loc.Language == "en" ? TitleEn : TitlePt;
        public string Text => Loc.Language == "en" ? TextEn : TextPt;
    }

    public sealed record Section(string TitlePt, string TitleEn, string[] ItemsPt, string[] ItemsEn)
    {
        public string Title => Loc.Language == "en" ? TitleEn : TitlePt;
        public string[] Items => Loc.Language == "en" ? ItemsEn : ItemsPt;
    }

    public static readonly Highlight[] Highlights =
    {
        new("dl", Color.FromRgb(0x5E, 0xD6, 0x7D), "Novo", "New", "Download Center", "Download Center",
            "Acompanhe os downloads direto na ilha, em qualquer pasta, e veja tudo na nova aba Download Center.",
            "Follow your downloads right on the island, in any folder, and see them all in the new Download Center tab."),
        new("claude", Color.FromRgb(0xE8, 0x82, 0x5F), "Claude", "Claude", "O Claude na ilha", "Claude on the island",
            "Veja o que o Claude está fazendo e responda pedidos de permissão sem trocar de janela.",
            "See what Claude is doing and answer permission requests without switching windows."),
        new("focus", Color.FromRgb(0xB7, 0x9C, 0xFF), "Foco", "Focus", "Um novo Foco", "A new Focus",
            "Anel de progresso, ajuste com a rodinha do mouse, tarefa, estatísticas e Não perturbe.",
            "A progress ring, mouse-wheel adjusting, a task, stats and Do not disturb."),
        new("icons", Color.FromRgb(0x7C, 0xC8, 0xFF), "Visual", "Visuals", "Mais fluido e mais vivo", "Smoother and livelier",
            "Animações mais suaves, ícones que mostram o que está acontecendo e transições entre a ilha e as bolhas.",
            "Smoother animations, icons that show what's going on and transitions between the island and its bubbles."),
        new("settings", Color.FromRgb(0xFF, 0xD3, 0x6B), "Configurações", "Settings", "Painel redesenhado", "A redesigned panel",
            "Organizado por categorias, com busca e descrições mais claras.",
            "Organized by category, with search and clearer descriptions."),
    };

    public static readonly Section[] Sections =
    {
        new("Download Center (novo)", "Download Center (new)",
            new[]
            {
                "Acompanhe o progresso dos downloads direto na ilha, com uma indicação luminosa no contorno.",
                "Compatível com Chrome, Edge, Firefox e Zen, em qualquer pasta de destino, e com as cópias de arquivos do Windows.",
                "Quando outra atividade ocupa a ilha, o download passa a ser exibido em uma bolha ao lado.",
                "Nova aba Download Center, com os downloads em andamento (abrir pasta e cancelar) e o histórico recente."
            },
            new[]
            {
                "Follow download progress right on the island, with a light around its edge.",
                "Works with Chrome, Edge, Firefox and Zen, in any destination folder, and with Windows file copies.",
                "When something else has the island, the download shows in a bubble next to it.",
                "New Download Center tab, with downloads in progress (open folder and cancel) and recent history."
            }),
        new("Claude", "Claude",
            new[]
            {
                "A ilha agora mostra o que o Claude está fazendo (pensando, lendo, editando, testando) com animações próprias.",
                "Responda pedidos de permissão (Permitir ou Negar) direto pela ilha, sem trocar de janela.",
                "Novo botão \"Abrir no Claude\", que leva à janela da sessão em andamento (app, terminal ou VS Code)."
            },
            new[]
            {
                "The island now shows what Claude is doing (thinking, reading, editing, testing) with its own animations.",
                "Answer permission requests (Allow or Deny) right on the island, without switching windows.",
                "New \"Open in Claude\" button, which takes you to the session's window (app, terminal or VS Code)."
            }),
        new("Foco", "Focus",
            new[]
            {
                "Aba redesenhada, com um anel de progresso.",
                "Ajuste a duração com a rodinha do mouse ou pelos atalhos de 10, 15, 30 e 60 minutos.",
                "Defina a tarefa da sessão e acompanhe o tempo focado no dia, a sequência de dias e o resumo da semana.",
                "Modo Não perturbe: o Notchy fica em silêncio durante o foco.",
                "Quando outra atividade ocupa a ilha, o Foco é exibido em uma bolha ao lado."
            },
            new[]
            {
                "Redesigned tab, with a progress ring.",
                "Set the length with the mouse wheel or the 10, 15, 30 and 60 minute shortcuts.",
                "Name the session's task and follow today's focus time, your streak and the week's summary.",
                "Do not disturb: Notchy stays quiet while you focus.",
                "When something else has the island, Focus shows in a bubble next to it."
            }),
        new("Visual e animações", "Visuals and animations",
            new[]
            {
                "Nova animação de abertura.",
                "Animações mais fluidas, acompanhando a taxa de atualização da tela.",
                "Ícones do menu animados para indicar atividades em andamento e concluídas.",
                "Transições suaves entre a ilha e as bolhas laterais.",
                "Cartões da tela inicial com efeito de inclinação em 3D.",
                "Prateleira com nova ilustração animada."
            },
            new[]
            {
                "New opening animation.",
                "Smoother animations that follow your screen's refresh rate.",
                "Animated menu icons for activities in progress and finished.",
                "Smooth transitions between the island and its side bubbles.",
                "Home cards with a 3D tilt effect.",
                "Shelf with a new animated illustration."
            }),
        new("Configurações", "Settings",
            new[]
            {
                "Painel totalmente redesenhado, organizado por categorias e com busca.",
                "Descrições mais claras para todas as opções.",
                "Pré-visualização dos efeitos sonoros."
            },
            new[]
            {
                "Fully redesigned panel, organized by category and with search.",
                "Clearer descriptions for every option.",
                "Sound effect previews."
            }),
        new("Correções", "Fixes",
            new[] { "Melhorias de nitidez em textos e ícones.", "Correções de bugs visuais e de desempenho." },
            new[] { "Sharper text and icons.", "Visual and performance bug fixes." }),
    };
}
