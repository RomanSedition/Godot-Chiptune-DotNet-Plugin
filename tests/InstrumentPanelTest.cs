using System.Collections.Generic;
using Godot;
using Godot.Collections;
using ChiptrackerNet.Engine;
using ChiptrackerNet.UI;

namespace ChiptrackerNet.Tests
{
    // C# counterpart of envelope_archetypes_test.gd's _test_panel --
    // covers the instrument panel's volume RANDOMIZE row (archetype
    // drop-down + button, keeping the envelope's current point count) and
    // the pitch envelope's own RANDOMIZE PITCH (independent Nodes/Range
    // boxes, never touching the volume envelope). Layer-tab wiring itself
    // is exercised separately, in LayerTabsTest.
    public partial class InstrumentPanelTest : SceneTree
    {
        public override async void _Initialize()
        {
            var failures = new List<string>();
            await TestPanel(failures);

            if (failures.Count == 0)
            {
                GD.Print("instrument_panel_test: PASS");
            }
            else
            {
                foreach (var f in failures)
                    GD.PrintErr($"instrument_panel_test: FAIL - {f}");
                GD.PrintErr($"instrument_panel_test: {failures.Count} failure(s)");
            }
            Quit(failures.Count == 0 ? 0 : 1);
        }

        static void Check(List<string> failures, bool condition, string label)
        {
            if (!condition)
                failures.Add(label);
        }

        async System.Threading.Tasks.Task TestPanel(List<string> failures)
        {
            var mainViewScene = GD.Load<PackedScene>("res://addons/chiptracker_net/ui/chiptracker_main_view.tscn");
            var view = mainViewScene.Instantiate<ChiptrackerMainView>();
            Root.AddChild(view);
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);

            var song = new Song();
            var instrument = new Instrument { Id = 0 };
            instrument.Envelope = new Array<float> { 1.0f, 0.8f, 0.6f, 0.4f, 0.2f, 0.0f };
            song.Instruments.Add(instrument);
            song.Channels.Add(new Channel());
            song.Patterns.Add(new Pattern(4, 1));
            song.OrderList.Add(0);
            view.SetSong(song);
            var panel = view._instrumentPanel;
            panel.EditInstrument(instrument);
            var edits = 0;
            panel.Connect(InstrumentPanel.SignalName.InstrumentEdited, Callable.From(() => edits++));

            Check(failures, panel._archetypeOption != null && panel._randomizeButton != null, "the panel has the drop-down and the button");
            Check(failures, panel._randomizeButton.Text == "RANDOMIZE", "the button is labelled RANDOMIZE");
            Check(failures, panel._archetypeOption.ItemCount == 20, "the drop-down lists the twenty archetypes");
            Check(failures, panel._archetypeOption.GetItemText(0) == EnvelopeArchetypes.ArchetypeName(0) && panel._archetypeOption.GetItemText(19) == EnvelopeArchetypes.ArchetypeName(19), "with their names, in order");

            // RANDOMIZE writes the chosen archetype, keeping the envelope's
            // current number of points -- there's no separate node-count box
            // for volume.
            panel._archetypeOption.Select(2); // the gated Atari kick
            instrument.Envelope = new Array<float> { 1.0f, 1.0f, 1.0f, 1.0f };
            instrument.EnvelopeTimes.Clear();
            panel._rng.Seed = 1;
            panel.OnRandomizePressed();
            Check(failures, instrument.Envelope.Count == 4 && instrument.EnvelopeTimes.Count == 4, "RANDOMIZE keeps the envelope's current number of points");
            Check(failures, instrument.HasCustomTimes() && Mathf.IsEqualApprox(instrument.EnvelopeTimes[2], instrument.EnvelopeTimes[3]), "with their times, in the archetype's shape (the gated kick's step)");
            Check(failures, panel._envelopeGraph.Values.Count == 4 && panel._envelopeGraph.Times.Count == 4, "and the graph shows them");
            Check(failures, edits > 0, "and the instrument is reported as edited");
            Check(failures, panel._envelopeCountLabel.Text.Contains("4"), "and the header shows the count");
            Check(failures, panel._envelopeGraph.SelectedIndex == 0, "and the first point is selected");

            // Another click gives another envelope of the same size.
            var before = new Array<float>(instrument.Envelope);
            var beforeTimes = new Array<float>(instrument.EnvelopeTimes);
            panel._rng.Seed = 2;
            panel.OnRandomizePressed();
            Check(failures, !ArraysEqual(instrument.Envelope, before) || !ArraysEqual(instrument.EnvelopeTimes, beforeTimes), "randomizing again gives a different envelope");
            Check(failures, instrument.Envelope.Count == 4, "still keeping the same point count");

            // Growing the envelope first (Add) makes RANDOMIZE follow the
            // bigger count.
            for (var i = 0; i < 56; i++)
                panel.OnAddEnvelopePointPressed(); // 4 -> 60
            panel.OnRandomizePressed();
            Check(failures, instrument.Envelope.Count == 60 && instrument.EnvelopeTimes.Count == 60, "growing the envelope first makes RANDOMIZE use 60 points");
            Check(failures, panel._envelopeGraph.Values.Count == 60, "and shows in the graph");

            // RANDOMIZE never touches the pitch envelope.
            var pitchBefore = new Array<float>(instrument.PitchEnvelope);
            panel.OnRandomizePressed();
            Check(failures, ArraysEqual(instrument.PitchEnvelope, pitchBefore), "the volume RANDOMIZE leaves the pitch envelope alone");

            // Nothing selected: the button does nothing.
            panel.EditInstrument(null);
            Check(failures, panel._randomizeButton.Disabled, "with no instrument the button is disabled");
            var kept = new Array<float>(instrument.Envelope);
            panel.OnRandomizePressed();
            Check(failures, ArraysEqual(instrument.Envelope, kept), "and pressing it changes nothing");

            // RANDOMIZE PITCH is independent: its own Nodes/Range boxes,
            // never touching the volume envelope.
            panel.EditInstrument(instrument);
            Check(failures, panel._pitchArchetypeOption != null && panel._pitchRandomizeButton != null, "the pitch section has its own drop-down and button");
            Check(failures, panel._pitchRandomizeButton.Text == "RANDOMIZE PITCH", "labelled RANDOMIZE PITCH");
            var volumeBefore = new Array<float>(instrument.Envelope);
            panel.OnPitchRandomizePressed();
            Check(failures, ArraysEqual(instrument.Envelope, volumeBefore), "RANDOMIZE PITCH leaves the volume envelope alone");
            Check(failures, instrument.PitchEnvelope.Count == 4, "RANDOMIZE PITCH used the default Nodes count");

            // Layer tabs: adding a layer creates a second tab and switches
            // the shared controls to edit it.
            Check(failures, panel._layerTabs.TabCount == 1, "a fresh instrument starts with only the Base tab");
            panel.OnAddLayerPressed();
            Check(failures, panel._layerTabs.TabCount == 2 && instrument.Layers.Count == 1, "+ LAYER adds a tab and a layer");
            Check(failures, panel._editedTarget == instrument.Layers[0], "adding a layer switches editing to it");
            panel.OnRemoveLayerPressed();
            Check(failures, panel._layerTabs.TabCount == 1 && instrument.Layers.Count == 0, "REMOVE LAYER removes it and its tab");

            view.QueueFree();
        }

        static bool ArraysEqual(Array<float> a, Array<float> b)
        {
            if (a.Count != b.Count)
                return false;
            for (var i = 0; i < a.Count; i++)
                if (!Mathf.IsEqualApprox(a[i], b[i]))
                    return false;
            return true;
        }
    }
}
