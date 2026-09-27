using System.Collections.Generic;
using Godot;
using ChiptrackerNet.Engine;
using ChiptrackerNet.UI;

namespace ChiptrackerNet.Tests
{
    // C# counterpart of tests/groups_test.gd -- covers the Channel-Groups
    // feature: Song/ChannelGroup data model (add/remove/merge, Group 1
    // protection), ChiptrackerDock's Groups tab wiring (add/remove/
    // rename/move channel), and PatternGrid's grouped column layout
    // (display order, group header band, partition gap).
    public partial class GroupsTest : SceneTree
    {
        public override async void _Initialize()
        {
            var failures = new List<string>();

            TestSongGroupModel(failures);
            await TestDockGroupsTab(failures);
            TestGridGrouping(failures);

            if (failures.Count == 0)
            {
                GD.Print("groups_test: PASS");
            }
            else
            {
                foreach (var f in failures)
                    GD.PrintErr($"groups_test: FAIL - {f}");
                GD.PrintErr($"groups_test: {failures.Count} failure(s)");
            }
            Quit(failures.Count == 0 ? 0 : 1);
        }

        static void Check(List<string> failures, bool condition, string label)
        {
            if (!condition)
                failures.Add(label);
        }

        static Song MakeSong(int channelCount = 4)
        {
            var song = new Song();
            for (var i = 0; i < channelCount; i++)
                song.Channels.Add(new Channel());
            song.Patterns.Add(new Pattern(4, channelCount));
            return song;
        }

        void TestSongGroupModel(List<string> failures)
        {
            var song = MakeSong();
            Check(failures, song.Groups.Count == 1, "a fresh Song starts with exactly one group");
            Check(failures, song.Channels[0].GroupIndex == 0, "a fresh Channel defaults into Group 1 (index 0)");

            var g1 = song.AddGroup();
            Check(failures, g1 == 1, "AddGroup returns the new group's index");
            Check(failures, song.Groups.Count == 2, "AddGroup grows the group list");

            var g2 = song.AddGroup();
            Check(failures, g2 == 2, "a third group lands at index 2");

            // Move channel 0 into group 1, channel 1 into group 2.
            song.Channels[0].GroupIndex = 1;
            song.Channels[1].GroupIndex = 2;

            // Group 1 (index 0) can never be removed.
            song.RemoveGroup(0);
            Check(failures, song.Groups.Count == 3, "RemoveGroup refuses to remove Group 1 (index 0)");

            // Removing group 2 (the last) merges its channel into group 1 (index 1).
            song.RemoveGroup(2);
            Check(failures, song.Groups.Count == 2, "RemoveGroup shrinks the group list");
            Check(failures, song.Channels[1].GroupIndex == 1, "removing a group folds its channels into the group to its left");
            Check(failures, song.Channels[0].GroupIndex == 1, "removing a group doesn't disturb channels already in the group it merges into");

            // Removing a middle group shifts every later channel's GroupIndex down.
            song.AddGroup(); // index 2
            song.Channels[2].GroupIndex = 2;
            song.RemoveGroup(1);
            Check(failures, song.Channels[0].GroupIndex == 0, "channels merged from the removed group land in index-1");
            Check(failures, song.Channels[2].GroupIndex == 1, "channels in a later group shift their GroupIndex down by one");

            // Can't remove down to zero groups.
            while (song.Groups.Count > 1)
                song.RemoveGroup(song.Groups.Count - 1);
            Check(failures, song.Groups.Count == 1, "sanity: down to one group");
            song.RemoveGroup(0);
            Check(failures, song.Groups.Count == 1, "RemoveGroup refuses to go below one remaining group");
        }

        async System.Threading.Tasks.Task TestDockGroupsTab(List<string> failures)
        {
            var mainViewScene = GD.Load<PackedScene>("res://addons/chiptracker_net/ui/chiptracker_main_view.tscn");
            var mainView = mainViewScene.Instantiate<ChiptrackerMainView>();
            var dockScene = GD.Load<PackedScene>("res://addons/chiptracker_net/ui/chiptracker_dock.tscn");
            var dock = dockScene.Instantiate<ChiptrackerDock>();
            dock.Size = new Vector2(1600, 300); // wide enough that /4 clears MinGroupColumnWidth
            Root.AddChild(mainView);
            Root.AddChild(dock);
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);
            mainView.SetDock(dock);

            Check(failures, dock._groupColumnsContainer.GetChildCount() == 1, "dock starts with one Group column (Group 1)");

            // Switch to the Groups tab so GroupsScroll is actually laid
            // out/sized -- column width is computed off its viewport
            // width, so this needs a real size to check against.
            dock.OnViewTabChanged(1);
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);

            foreach (var column in dock.Columns())
                Check(failures, !column.Visible, "every Channels-tab column hides while the Groups tab is showing");
            Check(failures, dock._body.Position.Y > 0.0f, "sanity: Body sits below TabBarRow, so column.Position alone (Body-relative) isn't self-relative");

            var quarterWidth = dock._groupsScroll.Size.X / 4.0f;
            Check(failures, quarterWidth > ChiptrackerDock.MinGroupColumnWidth, $"sanity: the dock is wide enough in this test for the /4 rule to be the binding one (got {quarterWidth})");
            var firstColumn = (Control)dock._groupColumnsContainer.GetChild(0);
            Check(failures, Mathf.IsEqualApprox(firstColumn.CustomMinimumSize.X, quarterWidth), $"with 1 group, its column is sized to a quarter of the panel width (got {firstColumn.CustomMinimumSize.X}, want {quarterWidth})");

            // Past 4 groups, columns stop growing narrower -- they keep
            // the same quarter-panel width and the extra ones just
            // overflow into scroll.
            for (var i = 0; i < 5; i++)
                dock.OnAddGroupPressed();
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);
            Check(failures, dock._groupColumnsContainer.GetChildCount() == 6, "sanity: 6 group columns now exist");
            var lastColumn = (Control)dock._groupColumnsContainer.GetChild(5);
            Check(failures, Mathf.IsEqualApprox(lastColumn.CustomMinimumSize.X, quarterWidth), "a 6th column keeps the same quarter-panel width rather than shrinking to fit");
            Check(failures, dock._groupColumnsContainer.GetCombinedMinimumSize().X > dock._groupsScroll.Size.X, "6 full-width columns overflow the scroll viewport, which is what enables horizontal scrolling");

            // Back down to one group for the rest of this test.
            while (mainView.Song.Groups.Count > 1)
            {
                dock._selectedGroupIndex = mainView.Song.Groups.Count - 1;
                dock.OnRemoveGroupPressed();
            }
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);

            dock.OnAddGroupPressed();
            Check(failures, mainView.Song.Groups.Count == 2, "Add builds a second group");
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);
            Check(failures, dock._groupColumnsContainer.GetChildCount() == 2, "dock rebuilds the Groups tab with the new column");
            Check(failures, !dock._removeGroupButton.Disabled, "Remove enables once there's more than one group");

            // Move channel 0 from Group 1 into Group 2 via the row's "->" button.
            dock.OnMoveChannelGroup(0, 1);
            Check(failures, mainView.Song.Channels[0].GroupIndex == 1, "-> moves the channel into the next group");
            dock.OnMoveChannelGroup(0, -1);
            Check(failures, mainView.Song.Channels[0].GroupIndex == 0, "<- moves it back into the previous group");

            // Rename Group 1.
            var renameEdit = new LineEdit { Text = "Melody" };
            dock.CommitGroupRename(0, renameEdit, new Button());
            Check(failures, mainView.Song.Groups[0].Name == "Melody", "group rename commit writes the new name");

            // Remove refuses Group 1 even when it's the selected/target index.
            dock._selectedGroupIndex = 0;
            dock.OnRemoveGroupPressed();
            Check(failures, mainView.Song.Groups.Count == 2, "Remove is a no-op when Group 1 is selected");

            // Remove targets the selected group otherwise, folding its channel back.
            mainView.Song.Channels[0].GroupIndex = 1;
            dock._selectedGroupIndex = 1;
            dock.OnRemoveGroupPressed();
            Check(failures, mainView.Song.Groups.Count == 1, "Remove shrinks the group list when a non-Group-1 group is selected");
            Check(failures, mainView.Song.Channels[0].GroupIndex == 0, "the removed group's channel merges back into Group 1");

            mainView.QueueFree();
            dock.QueueFree();
        }

        void TestGridGrouping(List<string> failures)
        {
            var song = MakeSong(4);
            // Channels 0,2 in Group 1 (index 0); channels 1,3 in a second
            // group -- display order should cluster them by group, not
            // raw channel index.
            song.AddGroup();
            song.Channels[1].GroupIndex = 1;
            song.Channels[3].GroupIndex = 1;

            var grid = new PatternGrid { Song = song, PatternIndex = 0 };

            var order = grid.ChannelDisplayOrder();
            Check(failures, order.Count == 4 && order[0] == 0 && order[1] == 2 && order[2] == 1 && order[3] == 3,
                $"display order clusters channels by group_index, preserving original order within a group (got [{string.Join(",", order)}])");

            var layout = grid.ChannelLayout();
            var xForChannel = layout.XForChannel;
            Check(failures, xForChannel[0] < xForChannel[2], "channel 0 sits left of channel 2 (same group, original order preserved)");
            Check(failures, xForChannel[2] < xForChannel[1], "the last channel of group 1 sits left of the first channel of group 2");
            Check(failures, xForChannel[1] - (xForChannel[2] + PatternGrid.ChannelWidth) == PatternGrid.GroupGap, "a GroupGap-wide, and only GroupGap-wide, partition separates the two groups");
            Check(failures, xForChannel[1] < xForChannel[3], "channel 1 sits left of channel 3 (same group, original order preserved)");

            // Left/Right arrow navigation should follow the same grouped
            // visual order, not raw channel-index arithmetic.
            Check(failures, grid.AdjacentChannel(2, 1) == 1, "Right from the last channel of group 1 steps into the first channel of group 2");
            Check(failures, grid.AdjacentChannel(0, -1) == 0, "Left from the first on-screen channel stays put");
            Check(failures, grid.AdjacentChannel(3, 1) == 3, "Right from the last on-screen channel stays put");

            // Clicking inside a GroupGap shouldn't resolve to either
            // neighboring channel.
            var gapX = xForChannel[2] + PatternGrid.ChannelWidth + PatternGrid.GroupGap / 2.0f;
            Check(failures, grid.ChannelAtX(gapX) == -1, "a click inside the partition gap doesn't select a channel");
            Check(failures, grid.ChannelAtX(xForChannel[1] + 4) == 1, "a click just inside a channel's column still resolves to it");

            grid.QueueFree();
        }
    }
}
