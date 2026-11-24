namespace Blocker.UI.Sdk;

/// <summary>Semantic colour role. The host maps tones to the application palette; plugins never choose colours.</summary>
public enum UiTone
{
    /// <summary>Default text and surfaces.</summary>
    Neutral,

    /// <summary>The single technical accent colour (active state).</summary>
    Accent,

    /// <summary>Positive outcome.</summary>
    Success,

    /// <summary>Amber/coral warning colour (also used for emergency passes).</summary>
    Warning,

    /// <summary>Destructive or failed state.</summary>
    Danger,
}

/// <summary>Typographic role of a text element.</summary>
public enum UiTextStyle
{
    /// <summary>Regular body text.</summary>
    Body,

    /// <summary>Small secondary text.</summary>
    Caption,

    /// <summary>Section subtitle.</summary>
    Subtitle,

    /// <summary>Section title.</summary>
    Title,

    /// <summary>Monospace text for numbers, timers and technical values.</summary>
    Mono,
}

/// <summary>Layout direction of a <see cref="UiStack"/>.</summary>
public enum UiOrientation
{
    /// <summary>Children are stacked top to bottom.</summary>
    Vertical,

    /// <summary>Children are laid out left to right.</summary>
    Horizontal,
}

/// <summary>Spacing between children; maps to the design system's 8/12/16 px gaps.</summary>
public enum UiGap
{
    /// <summary>8 px.</summary>
    Small,

    /// <summary>12 px.</summary>
    Medium,

    /// <summary>16 px.</summary>
    Large,
}

/// <summary>Visual weight of a <see cref="UiButton"/>.</summary>
public enum UiButtonVariant
{
    /// <summary>Accent-filled primary action.</summary>
    Primary,

    /// <summary>Default bordered button.</summary>
    Secondary,

    /// <summary>Amber warning action.</summary>
    Warning,

    /// <summary>Borderless, low-emphasis action.</summary>
    Subtle,
}

/// <summary>How a <see cref="UiToggle"/> is drawn.</summary>
public enum UiToggleStyle
{
    /// <summary>A switch at the right end of the row, for settings.</summary>
    Switch,

    /// <summary>A tick box in front of the label; a ticked label is struck through. For to-do style list rows.</summary>
    Checkbox,
}

/// <summary>Horizontal alignment of a table column.</summary>
public enum UiAlignment
{
    /// <summary>Left aligned.</summary>
    Start,

    /// <summary>Centred.</summary>
    Center,

    /// <summary>Right aligned (use for numbers).</summary>
    End,
}

/// <summary>Width of a dashboard tile in grid columns.</summary>
public enum UiWidgetSize
{
    /// <summary>One column.</summary>
    Small,

    /// <summary>Two columns.</summary>
    Medium,

    /// <summary>Full row.</summary>
    Wide,
}

/// <summary>Curated icon set. The host renders these with its own icon font so plugins match the app.</summary>
public enum UiIcon
{
    /// <summary>Clock.</summary>
    Clock,

    /// <summary>Timer.</summary>
    Timer,

    /// <summary>Shield.</summary>
    Shield,

    /// <summary>Closed lock.</summary>
    Lock,

    /// <summary>Open lock.</summary>
    Unlock,

    /// <summary>Play.</summary>
    Play,

    /// <summary>Pause.</summary>
    Pause,

    /// <summary>Stop.</summary>
    Stop,

    /// <summary>Warning triangle.</summary>
    Warning,

    /// <summary>Information.</summary>
    Info,

    /// <summary>Checkmark.</summary>
    Checkmark,

    /// <summary>Dismiss / close.</summary>
    Dismiss,

    /// <summary>Settings gear.</summary>
    Settings,

    /// <summary>Bar chart.</summary>
    Chart,

    /// <summary>Calendar.</summary>
    Calendar,

    /// <summary>App grid.</summary>
    Apps,

    /// <summary>Globe.</summary>
    Globe,

    /// <summary>Plug.</summary>
    Plug,

    /// <summary>Flame.</summary>
    Flame,

    /// <summary>Leaf.</summary>
    Leaf,

    /// <summary>Target.</summary>
    Target,

    /// <summary>Star.</summary>
    Star,

    /// <summary>Heart.</summary>
    Heart,

    /// <summary>Book.</summary>
    Book,

    /// <summary>Code brackets.</summary>
    Code,

    /// <summary>Music note.</summary>
    Music,

    /// <summary>Game controller.</summary>
    Game,

    /// <summary>Moon.</summary>
    Moon,

    /// <summary>Sun.</summary>
    Sun,

    /// <summary>Coffee cup.</summary>
    Coffee,

    /// <summary>Folder.</summary>
    Folder,

    /// <summary>Trophy.</summary>
    Trophy,

    /// <summary>Pencil.</summary>
    Edit,

    /// <summary>Two stacked sheets.</summary>
    Copy,

    /// <summary>Bin.</summary>
    Delete,
}

/// <summary>How a <see cref="UiFormField"/> is drawn.</summary>
public enum UiFieldStyle
{
    /// <summary>A labelled field.</summary>
    Standard,

    /// <summary>A large borderless text input with an underline and no visible label; only on <see cref="UiTextField"/>.</summary>
    Title,

    /// <summary>A short text input; consecutive compact fields share one line, joined by a dash; only on <see cref="UiTextField"/>.</summary>
    Compact,

    /// <summary>Options as a row of chips instead of a drop-down; only on <see cref="UiSelectField"/> with 2 to 4 options.</summary>
    Chips,
}

/// <summary>Kind of user interaction reported in a <see cref="UiAction"/>.</summary>
public enum UiActionKind
{
    /// <summary>A <see cref="UiButton"/> was clicked.</summary>
    Click,

    /// <summary>A <see cref="UiToggle"/> changed state.</summary>
    Toggle,

    /// <summary>A <see cref="UiForm"/> was submitted.</summary>
    Submit,

    /// <summary>A block of a <see cref="UiTimeGrid"/> was created, moved or resized; <c>FormValues</c> holds <c>day</c>, <c>start</c> and <c>end</c>.</summary>
    Schedule,

    /// <summary>An empty tile of a <see cref="UiScene"/> with <c>AllowPick</c> was clicked; <c>FormValues</c> holds <c>column</c> and <c>row</c>.</summary>
    Pick,
}
