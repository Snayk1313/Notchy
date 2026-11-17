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
    public const string Version = "2.4";

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
        new("capture", Color.FromRgb(0x9A, 0xD7, 0xFF), "Novo", "New", "Captura de tela", "Screen capture",
            "Print Screen abre a captura do Notchy: escolha a área, desenhe por cima e o print sobe até a ilha.",
            "Print Screen opens Notchy's capture: pick the area, draw on it and the picture flies up to the island."),
        new("pin", Color.FromRgb(0x9A, 0xD7, 0xFF), "Novo", "New", "Print fixado na tela", "Pinned capture",
            "Deixe um print por cima das janelas. Redimensione pelos cantos e dê zoom com a rodinha.",
            "Keep a capture over your windows. Resize it by the corners and zoom with the wheel."),
        new("shelf", Color.FromRgb(0x6E, 0xE7, 0xC4), "Melhor", "Better", "Prateleira animada", "Livelier shelf",
            "A caixinha pega os primeiros arquivos no ar, e o Limpar estoura tudo em faíscas que ela recolhe.",
            "The little box catches the first files mid-air, and Clear pops them into sparks it gathers back."),
        new("scroll", Color.FromRgb(0xFF, 0xFF, 0xFF), "Melhor", "Better", "Listas mais suaves", "Smoother lists",
            "Todas as listas deslizam com a rodinha e esmaecem na borda que ainda tem mais para ver.",
            "Every list glides with the wheel and fades at the edge that has more to see."),
    };

    public static readonly Section[] Sections =
    {
        new("Captura de tela (nova)", "Screen capture (new)",
            new[]
            {
                "O Print Screen abre a captura do Notchy no lugar da do Windows. A tela congela, e a ilha sai no print como em qualquer app.",
                "Arraste uma área ou clique numa janela, que acende quando o mouse passa por ela. A lupa ajuda a acertar o pixel e as medidas ficam ao lado do cursor.",
                "A ilha se solta e desce até a área virando a barra de ferramentas, com o mesmo vidro da sua ilha e os olhinhos espiando.",
                "Caneta, seta, retângulo, marca-texto, texto e pixelar para esconder dados, com cores, espessura e Ctrl + Z.",
                "Copie (Enter ou Ctrl + C), salve escolhendo a pasta (ela lembra a última), fixe na tela ou copie o texto da imagem.",
                "O print sobe até a ilha com um rastro de luz e ela se abre mostrando a miniatura, com só o que falta: salvar, copiar ou editar de novo.",
                "Print fixado: fica por cima das janelas, redimensiona pelos cantos, a rodinha dá zoom, Espaço + arrastar move a imagem e um clique na porcentagem volta para 100%.",
                "Escolha o atalho em Configurações → Arquivos: Print Screen, uma combinação ou uma tecla sozinha como Pause ou F1 a F24."
            },
            new[]
            {
                "Print Screen opens Notchy's capture instead of the Windows one. The screen freezes, and the island shows in the picture like in any capture app.",
                "Drag an area or click a window, which lights up under the mouse. The magnifier helps you hit the pixel and the size follows the cursor.",
                "The island lets go and comes down to the area as the toolbar, with your island's own glass and its eyes peeking out.",
                "Pen, arrow, rectangle, highlighter, text and pixelate to hide data, with colours, size and Ctrl + Z.",
                "Copy (Enter or Ctrl + C), save choosing the folder (it remembers the last one), pin it on screen or copy the text in it.",
                "The picture flies up to the island with a trail of light and it opens showing the thumbnail, with only what's left: save, copy or edit again.",
                "Pinned capture: stays over your windows, resizes by the corners, the wheel zooms, Space + drag moves the picture and a click on the percentage goes back to 100%.",
                "Pick the shortcut in Settings → Files: Print Screen, a combination or a key alone like Pause or F1 to F24."
            }),
        new("Prateleira", "Shelf",
            new[]
            {
                "Arrastando arquivos, os olhos da caixinha se arregalam e seguem. Ao soltar os primeiros, ela pula para pegá-los, solta faíscas e os arquivos se espalham do centro.",
                "O Limpar estoura os arquivos em faíscas, e a caixinha aparece para recolhê-las.",
                "Com muitos arquivos, a prateleira ganhou barra de rolagem e a rodinha anda um arquivo por vez."
            },
            new[]
            {
                "Dragging files, the little box's eyes go wide and follow them. Dropping the first ones, it jumps to catch them, throws sparks and the files spread out from the middle.",
                "Clear pops the files into sparks, and the little box comes to gather them.",
                "With many files, the shelf got a scroll bar and the wheel walks one file at a time."
            }),
        new("Listas e rolagem", "Lists and scrolling",
            new[]
            {
                "Todas as listas da ilha e das Configurações deslizam suavemente com a rodinha, como a fileira dos alarmes.",
                "Os itens ficam transparentes na borda que ainda tem mais para rolar."
            },
            new[]
            {
                "Every list on the island and in Settings glides smoothly with the wheel, like the alarms' row.",
                "Items fade at the edge that still has more to scroll."
            }),
        new("Primeira abertura", "First run",
            new[]
            {
                "O assistente ganhou a etapa Captura de tela, com o atalho para escolher na hora.",
                "A dica do volume no tour agora explica o Shift com a ilha aberta."
            },
            new[]
            {
                "The assistant got a Screen capture step, with the shortcut to pick right there.",
                "The volume tip in the tour now explains Shift on the open island."
            }),
        new("Correções", "Fixes",
            new[]
            {
                "A ilha não some mais quando uma janela do próprio Notchy ocupa a tela toda, como as Configurações maximizadas.",
                "O brilho de \u201ccopiado\u201d acompanha a ilha se ela muda de tamanho enquanto ele está aceso.",
                "A borda dos arquivos marcados na prateleira não fica mais cortada."
            },
            new[]
            {
                "The island no longer disappears when one of Notchy's own windows fills the screen, like a maximised Settings.",
                "The \u201ccopied\u201d glow follows the island if it changes size while it's on.",
                "The frame of marked files on the shelf is no longer cut."
            }),
    };
}
