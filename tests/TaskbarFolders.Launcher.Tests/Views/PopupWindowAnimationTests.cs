using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FluentAssertions;
using Moq;
using TaskbarFolders.Core.Icons;
using TaskbarFolders.Launcher.Configuration;
using TaskbarFolders.Launcher.Services;
using TaskbarFolders.Launcher.ViewModels;
using TaskbarFolders.Launcher.Views;
using TaskbarFolders.Shared.Configuration;
using TaskbarFolders.Shared.Models;
using Xunit;

namespace TaskbarFolders.Launcher.Tests.Views;

/// <summary>
/// Pins the open animation's start state to what <see cref="PopupWindow"/> paints first.
/// </summary>
/// <remarks>
/// The popup's first frame is drawn from the XAML defaults on <c>ChromeRoot</c>; the
/// storyboard only takes over one dispatcher cycle later. The two are declared in different
/// places in the same file, and nothing but this test keeps them equal: when they drift
/// apart the popup visibly jumps between its first frame and the first animation tick, and
/// when the defaults fall back to fully transparent the first frame shows nothing at all —
/// which is what made the popup feel slow in the first place.
///
/// The values live in XAML, so this needs the real window. It is built on an STA thread and
/// never shown: reading resources and named elements needs no HWND.
/// </remarks>
public sealed class PopupWindowAnimationTests
{
    [Fact]
    public void OpenAnimation_StartsFromTheStateTheFirstFramePaints()
    {
        OnStaThread(() =>
        {
            var window = CreatePopup();
            try
            {
                var chrome = (Border)window.FindName("ChromeRoot");
                var scale = (ScaleTransform)chrome.RenderTransform;
                var storyboard = (Storyboard)window.Resources["OpenAnimation"];

                storyboard.Children.Should().HaveCount(3, "opacity, horizontal scale and vertical scale");

                foreach (var timeline in storyboard.Children)
                {
                    var animation = (DoubleAnimation)timeline;
                    var target = TargetPropertyName(animation);
                    var paintedFirst = target switch
                    {
                        "Opacity" => chrome.Opacity,
                        "ScaleX" => scale.ScaleX,
                        "ScaleY" => scale.ScaleY,
                        _ => throw new InvalidOperationException($"OpenAnimation animates '{target}', which this test does not know."),
                    };

                    animation.From.Should().Be(
                        paintedFirst,
                        $"the first frame is painted from ChromeRoot's XAML value for '{target}', so the "
                        + "animation has to start exactly there or the popup jumps on its first tick");
                    animation.To.Should().Be(1.0, "the animation ends at the popup's normal appearance");
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void FirstFrame_IsAlreadyVisible()
    {
        OnStaThread(() =>
        {
            var window = CreatePopup();
            try
            {
                var chrome = (Border)window.FindName("ChromeRoot");

                chrome.Opacity.Should().BeGreaterThanOrEqualTo(
                    0.5,
                    "the first frame arrives hundreds of milliseconds after the click; if it is "
                    + "transparent the user waits through the fade as well");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Name of the property an animation ends up writing. A type-qualified path such as
    /// <c>(UIElement.RenderTransform).(ScaleTransform.ScaleX)</c> comes out of compiled XAML
    /// as the placeholder string <c>(0).(1)</c> with the properties as parameters, so the
    /// path text alone does not say what is animated.
    /// </summary>
    private static string TargetPropertyName(DoubleAnimation animation)
    {
        var path = Storyboard.GetTargetProperty(animation);
        return path.PathParameters.Count > 0
            ? ((DependencyProperty)path.PathParameters[^1]).Name
            : path.Path;
    }

    private static PopupWindow CreatePopup()
    {
        var viewModel = new PopupViewModel(
            Mock.Of<IGroupConfigStore>(),
            Mock.Of<IIconExtractor>(),
            Mock.Of<IIconCache>(),
            Mock.Of<IProcessLauncher>(),
            new LauncherOptions("g1"));

        return new PopupWindow(viewModel, Mock.Of<ITaskbarPositionHelper>(), new AppSettings());
    }

    private static void OnStaThread(Action body)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new InvalidOperationException("STA test body failed.", failure);
        }
    }
}
