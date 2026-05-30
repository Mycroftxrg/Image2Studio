using Image2Studio.Services;
using Microsoft.Maui.Controls.Shapes;

namespace Image2Studio;

public sealed class UpdatePromptPage : ContentPage
{
    private readonly TaskCompletionSource<bool> _completion = new();
    private bool _isClosing;

    private UpdatePromptPage(UpdateCheckResult update, string markdown)
    {
        Title = "发现新版本";
        BackgroundColor = Color.FromArgb("#66000000");
        Padding = new Thickness(16);

        var titleLabel = new Label
        {
            Text = "发现新版本",
            FontSize = 22,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#0F172A")
        };

        var versionLabel = new Label
        {
            Text = $"当前版本：{update.CurrentVersion}    最新版本：{update.LatestVersion}",
            FontSize = 14,
            TextColor = Color.FromArgb("#475569")
        };

        var markdownStack = new VerticalStackLayout
        {
            Spacing = 8,
            Padding = new Thickness(0, 4, 0, 4)
        };
        RenderMarkdown(markdownStack, markdown);

        var scrollView = new ScrollView
        {
            Content = markdownStack,
            HeightRequest = 360
        };

        var cancelButton = new Button
        {
            Text = "稍后",
            BackgroundColor = Color.FromArgb("#E2E8F0"),
            TextColor = Color.FromArgb("#0F172A"),
            CornerRadius = 8,
            Padding = new Thickness(18, 10)
        };
        cancelButton.Clicked += async (_, _) => await CloseAsync(false);

        var downloadButton = new Button
        {
            Text = "下载安装包",
            BackgroundColor = Color.FromArgb("#2563EB"),
            TextColor = Colors.White,
            CornerRadius = 8,
            Padding = new Thickness(18, 10)
        };
        downloadButton.Clicked += async (_, _) => await CloseAsync(true);

        var buttons = new HorizontalStackLayout
        {
            Spacing = 10,
            HorizontalOptions = LayoutOptions.End,
            Children =
            {
                cancelButton,
                downloadButton
            }
        };

        var panel = new Border
        {
            Stroke = Color.FromArgb("#CBD5E1"),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            BackgroundColor = Colors.White,
            MaximumWidthRequest = 720,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center,
            Padding = new Thickness(22),
            Content = new VerticalStackLayout
            {
                Spacing = 14,
                Children =
                {
                    titleLabel,
                    versionLabel,
                    new BoxView { HeightRequest = 1, Color = Color.FromArgb("#E2E8F0") },
                    scrollView,
                    buttons
                }
            }
        };

        Content = new Grid
        {
            Children =
            {
                panel
            }
        };
    }

    public static async Task<bool> ShowAsync(Page owner, UpdateCheckResult update, string markdown)
    {
        var prompt = new UpdatePromptPage(update, markdown);
        await owner.Navigation.PushModalAsync(prompt);
        return await prompt._completion.Task;
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync(false);
        return true;
    }

    private async Task CloseAsync(bool result)
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        _completion.TrySetResult(result);
        if (Navigation.ModalStack.Contains(this))
        {
            await Navigation.PopModalAsync();
        }
    }

    private static void RenderMarkdown(VerticalStackLayout stack, string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            stack.Children.Add(CreateBodyLabel("此版本未提供更新日志。"));
            return;
        }

        foreach (var rawLine in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                stack.Children.Add(new BoxView { HeightRequest = 4, Opacity = 0 });
                continue;
            }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("### ", StringComparison.Ordinal))
            {
                stack.Children.Add(CreateHeadingLabel(trimmed[4..], 16));
            }
            else if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                stack.Children.Add(CreateHeadingLabel(trimmed[3..], 18));
            }
            else if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                stack.Children.Add(CreateHeadingLabel(trimmed[2..], 20));
            }
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                stack.Children.Add(CreateBodyLabel($"• {trimmed[2..]}"));
            }
            else
            {
                stack.Children.Add(CreateBodyLabel(trimmed));
            }
        }
    }

    private static Label CreateHeadingLabel(string text, double fontSize)
    {
        return new Label
        {
            Text = StripInlineMarkdown(text),
            FontSize = fontSize,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#0F172A"),
            LineBreakMode = LineBreakMode.WordWrap
        };
    }

    private static Label CreateBodyLabel(string text)
    {
        return new Label
        {
            Text = StripInlineMarkdown(text),
            FontSize = 14,
            TextColor = Color.FromArgb("#334155"),
            LineBreakMode = LineBreakMode.WordWrap
        };
    }

    private static string StripInlineMarkdown(string text)
    {
        return text.Replace("**", string.Empty).Replace("__", string.Empty).Trim();
    }
}
