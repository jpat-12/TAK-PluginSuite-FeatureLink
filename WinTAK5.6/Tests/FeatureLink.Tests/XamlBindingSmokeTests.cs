using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace FeatureLink.Tests
{
    /// <summary>
    /// C-16 regression guard.
    ///
    /// <c>StatusText</c> is the plugin's only user-facing error and progress channel — assigned in
    /// 20+ places on sign-in failure, session expiry, layer load, add-layer failure, watcher
    /// failure, import failure, share failure, marker visibility/removal failure, download failure
    /// and send-to-layer. It was bound by <b>nothing</b> in FeatureLinkView.xaml. The only other
    /// occurrence of the token in that file is a <c>Style</c> resource key of the same name, a
    /// coincidence that made the omission invisible on review, so every one of those failures was
    /// silently swallowed and the operator saw nothing at all.
    ///
    /// This test asserts that every public string property on the view model is referenced by at
    /// least one <c>{Binding …}</c> in the XAML, and that every binding path in the XAML resolves
    /// to a real member. It works on the <b>source text</b> rather than by reflection because
    /// <c>FeatureLinkDockPane</c> derives from <c>WinTak.Framework.Docking.DockPane</c> and cannot
    /// be loaded without the WinTAK SDK — which is precisely the constraint that kept this
    /// codebase untested. Text analysis is coarser than reflection but it catches the exact defect
    /// that shipped, and it runs anywhere.
    /// </summary>
    public class XamlBindingSmokeTests
    {
        private readonly ITestOutputHelper _out;
        public XamlBindingSmokeTests(ITestOutputHelper output) { _out = output; }

        private static string ViewModelSource =>
            File.ReadAllText(RepoLocator.Path0("WinTAK5.6", "ViewModels", "FeatureLinkDockPane.cs"));

        private static string XamlSource =>
            File.ReadAllText(RepoLocator.Path0("WinTAK5.6", "Views", "FeatureLinkView.xaml"));

        /// <summary>Public auto/expression properties declared on the view model. Deliberately
        /// tolerant: it matches `public &lt;type&gt; &lt;Name&gt;` followed by a block or an
        /// expression body.</summary>
        private static readonly Regex PublicProperty = new Regex(
            @"^\s*public\s+(?<type>[A-Za-z_][\w<>,\.\[\]\? ]*?)\s+(?<name>[A-Z]\w*)\s*(=>|\{)",
            RegexOptions.Multiline | RegexOptions.Compiled);

        /// <summary>Captures the whole dotted path, because the layer-row DataTemplate reaches the
        /// dock-pane commands through <c>DataContext.DownloadLayerCommand</c> on a
        /// <c>RelativeSource AncestorType=ItemsControl</c> — a leading-segment-only match would
        /// have reported every per-row command as unbound.</summary>
        private static readonly Regex BindingPath = new Regex(
            @"\{Binding\s+(?:Path\s*=\s*)?(?<path>[A-Za-z_][\w\.]*)", RegexOptions.Compiled);

        private static List<(string Type, string Name)> PublicProperties()
        {
            var list = new List<(string, string)>();
            foreach (Match m in PublicProperty.Matches(ViewModelSource))
            {
                string type = m.Groups["type"].Value.Trim();
                string name = m.Groups["name"].Value;
                // Skip method-ish and modifier noise the coarse regex can pick up.
                if (type == "class" || type == "sealed" || type == "static" || type == "override") continue;
                list.Add((type, name));
            }
            return list;
        }

        /// <summary>Every identifier that appears anywhere in a binding path, so a dotted path
        /// contributes each of its segments.</summary>
        private static HashSet<string> BoundPaths()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in BindingPath.Matches(XamlSource))
                foreach (var segment in m.Groups["path"].Value.Split('.'))
                    if (segment.Length > 0) set.Add(segment);
            return set;
        }

        [Fact]
        public void StatusText_IsActuallyBound_NotJustAStyleKey()
        {
            // The specific defect. Guarded on its own so the failure message is unambiguous.
            Assert.Contains("{Binding StatusText", XamlSource, StringComparison.Ordinal);
        }

        [Fact]
        public void EveryPublicStringPropertyOnTheViewModel_IsReferencedByABinding()
        {
            var bound = BoundPaths();
            var unbound = PublicProperties()
                .Where(p => p.Type == "string")
                .Select(p => p.Name)
                .Where(name => !bound.Contains(name))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            foreach (var n in unbound) _out.WriteLine("UNBOUND string property: " + n);

            Assert.True(unbound.Count == 0,
                "These public string properties on FeatureLinkDockPane are not referenced by any "
                + "binding in FeatureLinkView.xaml, so whatever they carry can never reach the "
                + "operator: " + string.Join(", ", unbound));
        }

        [Fact]
        public void EveryPublicIcommandOnTheViewModel_IsReferencedByABinding()
        {
            var bound = BoundPaths();
            var unbound = PublicProperties()
                .Where(p => p.Type == "ICommand")
                .Select(p => p.Name)
                .Where(name => !bound.Contains(name))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            foreach (var n in unbound) _out.WriteLine("UNBOUND command: " + n);

            Assert.True(unbound.Count == 0,
                "These commands are unreachable from the UI: " + string.Join(", ", unbound));
        }

        [Fact]
        public void EveryBindingPathInTheXaml_ResolvesToAMemberOfTheViewModel()
        {
            // Paths that legitimately target something other than the dock-pane view model:
            // the layer-row DataTemplate binds ArcGisLayer/RecentCotItem members, and a few bind
            // WPF-intrinsic properties through RelativeSource.
            var itemMembers = new HashSet<string>(StringComparer.Ordinal)
            {
                // ArcGisLayer
                "Name", "Url", "Type", "IsPrivate", "Access", "HasDisplayConfig",
                "FeatureCount", "LastSyncTicks", "ActionGlyph", "DownloadEnabled",
                "RecurrenceInterval", "RecurrenceUnit", "IsPliLayer", "Visible", "EyeIconSource",
                // RecentCotItem
                "Uid", "Callsign", "CotType", "LastSeen", "LastSeenLabel",
                // WPF/BCL intrinsics reached via RelativeSource, PlacementTarget or a collection
                "DataContext", "PlacementTarget", "IsChecked", "SelectedItem", "Count",
            };

            var vmMembers = new HashSet<string>(PublicProperties().Select(p => p.Name), StringComparer.Ordinal);
            var unresolved = BoundPaths()
                .Where(p => !vmMembers.Contains(p) && !itemMembers.Contains(p))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            foreach (var n in unresolved) _out.WriteLine("UNRESOLVED binding path: " + n);

            Assert.True(unresolved.Count == 0,
                "These binding paths in FeatureLinkView.xaml do not resolve to any known member — "
                + "WPF fails such a binding silently: " + string.Join(", ", unresolved));
        }

        [Fact]
        public void TheParserItselfFindsTheExpectedShapeOfTheViewModel()
        {
            // Guards against the regexes silently matching nothing and the suite passing vacuously.
            var props = PublicProperties();
            Assert.True(props.Count > 30, "Only found " + props.Count + " public properties — the parser is broken.");
            Assert.Contains(props, p => p.Name == "StatusText" && p.Type == "string");
            Assert.True(BoundPaths().Count > 30, "Only found " + BoundPaths().Count + " bindings — the parser is broken.");
        }

        [Fact]
        public void TheStatusStripLivesOutsideTheScrollingPageContainer()
        {
            // If the strip were inside a page's ScrollViewer it could be scrolled out of sight,
            // which for the plugin's only error channel is nearly as bad as not binding it.
            string xaml = XamlSource;
            int strip = xaml.IndexOf("{Binding StatusText", StringComparison.Ordinal);
            int overlay = xaml.IndexOf("Overlay container", StringComparison.Ordinal);
            Assert.True(strip > 0, "status strip binding not found");
            Assert.True(strip < overlay, "the status strip must sit in the main grid, before the overlay");
            Assert.Contains("Grid.Row=\"2\"", xaml.Substring(Math.Max(0, strip - 400), 400));
        }
    }
}
