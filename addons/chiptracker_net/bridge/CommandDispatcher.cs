using System;
using Godot;
using Godot.Collections;
using ChiptrackerNet.Engine;

namespace ChiptrackerNet.Bridge
{
    // C# counterpart of addons/chiptracker/bridge/command_dispatcher.gd.
    // Translates parsed MCP bridge commands into calls on the engine
    // layer. Song defaults to a private in-memory Song (the McpBridge/
    // Play-time use case); EditorBridge instead points Song at a shared
    // instance and sets OnSongMutated so external tool calls can refresh
    // a live UI -- same dispatcher, same command set, different target.
    [Tool]
    public partial class CommandDispatcher : Node
    {
        public Song Song = new();

        // Called after any state-changing command, with `true` if Song
        // itself was replaced (create_song) rather than just mutated in
        // place. Null in the Play-time/private-song case, where there's
        // no UI to refresh.
        public Action<bool> OnSongMutated;

        AudioStreamPlayer _previewPlayer;
        PlaybackEngine _previewEngine;

        public override void _Ready()
        {
            if (_previewPlayer == null)
            {
                _previewPlayer = new AudioStreamPlayer();
                AddChild(_previewPlayer);
            }
            if (_previewEngine == null)
            {
                _previewEngine = new PlaybackEngine();
                AddChild(_previewEngine);
            }
        }

        // Use an already-existing engine/player instead of creating
        // private ones. Call before this node enters the tree.
        public void SetPreviewTarget(PlaybackEngine engine, AudioStreamPlayer player)
        {
            _previewEngine = engine;
            _previewPlayer = player;
        }

        public Dictionary Dispatch(string command, Dictionary parameters)
        {
            return command switch
            {
                "create_song" => CreateSong(parameters),
                "load_song" => LoadSong(parameters),
                "get_song" => Ok(SongToDict()),
                "add_instrument" => AddInstrument(parameters),
                "update_instrument" => UpdateInstrument(parameters),
                "rename_instrument" => RenameInstrument(parameters),
                "remove_instrument" => RemoveInstrument(parameters),
                "duplicate_instrument" => DuplicateInstrument(parameters),
                "add_layer" => AddLayer(parameters),
                "update_layer" => UpdateLayer(parameters),
                "remove_layer" => RemoveLayer(parameters),
                "list_archetypes" => ListArchetypes(),
                "random_instrument" => RandomInstrument(parameters),
                "add_channel" => AddChannel(parameters),
                "remove_channel" => RemoveChannel(parameters),
                "rename_channel" => RenameChannel(parameters),
                "update_channel" => UpdateChannel(parameters),
                "move_channel_to_group" => MoveChannelToGroup(parameters),
                "add_group" => AddGroup(),
                "remove_group" => RemoveGroup(parameters),
                "rename_group" => RenameGroup(parameters),
                "add_metronome" => AddMetronome(),
                "remove_metronome" => RemoveMetronome(),
                "add_pattern" => AddPattern(),
                "remove_pattern" => RemovePattern(parameters),
                "rename_pattern" => RenamePattern(parameters),
                "add_to_order" => AddToOrder(parameters),
                "remove_from_order" => RemoveFromOrder(parameters),
                "move_order_entry" => MoveOrderEntry(parameters),
                "set_note" => SetNote(parameters),
                "set_tempo" => SetTempo(parameters),
                "set_rows_per_pattern" => SetRowsPerPattern(parameters),
                "play_preview" => PlayPreview(parameters),
                "stop_preview" => StopPreview(),
                "render_wav" => RenderWav(parameters),
                _ => Err($"unknown command: {command}"),
            };
        }

        static Dictionary Ok(Dictionary result) => new() { ["ok"] = true, ["result"] = result };
        static Dictionary Err(string message) => new() { ["ok"] = false, ["error"] = message };

        void NotifyMutated(bool fullReset) => OnSongMutated?.Invoke(fullReset);

        static Array<float> FloatArray(Variant v)
        {
            var result = new Array<float>();
            if (v.VariantType == Variant.Type.Array)
            {
                foreach (var item in v.AsGodotArray())
                    result.Add(item.AsSingle());
            }
            return result;
        }

        static bool TimesValid(Array<float> times, Array<float> values, out string error)
        {
            error = null;
            if (times.Count == 0)
                return true;
            if (times.Count != values.Count)
            {
                error = "times must have one entry per envelope point";
                return false;
            }
            for (var i = 0; i < times.Count; i++)
            {
                if (times[i] < 0.0f || times[i] > 1.0f || (i > 0 && times[i] < times[i - 1]))
                {
                    error = "times must be between 0 and 1 and never decrease";
                    return false;
                }
            }
            return true;
        }

        Dictionary CreateSong(Dictionary parameters)
        {
            var channels = new Array<Channel>();
            if (parameters.ContainsKey("channels"))
            {
                foreach (var chVariant in parameters["channels"].AsGodotArray())
                {
                    var chData = chVariant.AsGodotDictionary();
                    var channel = new Channel
                    {
                        Name = chData.ContainsKey("name") ? chData["name"].AsString() : "",
                        InstrumentType = chData.ContainsKey("instrument_type") ? chData["instrument_type"].AsString() : "square",
                    };
                    channels.Add(channel);
                }
            }
            if (channels.Count == 0)
                return Err("create_song requires at least one channel");

            var newSong = new Song
            {
                Tempo = parameters.ContainsKey("tempo") ? parameters["tempo"].AsInt32() : 120,
                RowsPerBeat = parameters.ContainsKey("rows_per_beat") ? parameters["rows_per_beat"].AsInt32() : 4,
                RowsPerPattern = parameters.ContainsKey("rows_per_pattern") ? parameters["rows_per_pattern"].AsInt32() : 16,
                Channels = channels,
            };

            Song = newSong;
            NotifyMutated(true);
            return Ok(SongToDict());
        }

        // Replaces the current song by loading a Song resource wholesale
        // from a res:// path, instead of building it up one command at a
        // time -- for bulk imports where hundreds of individual set_note
        // calls would be impractical.
        Dictionary LoadSong(Dictionary parameters)
        {
            if (!parameters.ContainsKey("path"))
                return Err("load_song requires path");
            var path = parameters["path"].AsString();
            if (!ResourceLoader.Exists(path))
                return Err($"resource not found: {path}");
            var loaded = GD.Load(path);
            if (loaded is not Song loadedSong)
                return Err($"resource at {path} is not a Song");
            Song = loadedSong;
            NotifyMutated(true);
            return Ok(SongToDict());
        }

        Dictionary AddInstrument(Dictionary parameters)
        {
            var result = BuildInstrument(parameters, true, out var error);
            if (result == null)
                return Err(error);
            result.Id = Song.NextInstrumentId();
            Song.Instruments.Add(result);
            NotifyMutated(false);
            return Ok(new Dictionary { ["instrument_id"] = result.Id });
        }

        // Builds an Instrument from `parameters` (waveform, duty_cycle,
        // envelope, envelope_times, pitch_envelope, pitch_envelope_times,
        // pitch_range), and, when `allowLayers`, an optional "layers"
        // array of these same dicts, one per layer, each also taking
        // step_offset/fixed_note_enabled/fixed_note. Layers are never
        // allowed layers of their own regardless of `allowLayers` --
        // layering is one level deep. Returns null and sets `error` on
        // failure (envelope/pitch_envelope_times malformed, at any depth).
        Instrument BuildInstrument(Dictionary parameters, bool allowLayers, out string error)
        {
            error = null;
            var instrument = new Instrument
            {
                Waveform = parameters.ContainsKey("waveform") ? parameters["waveform"].AsString() : "square",
                DutyCycle = parameters.ContainsKey("duty_cycle") ? parameters["duty_cycle"].AsSingle() : 0.5f,
            };
            var envelope = parameters.ContainsKey("envelope") ? FloatArray(parameters["envelope"]) : new Array<float>();
            instrument.Envelope = envelope;
            var times = parameters.ContainsKey("envelope_times") ? FloatArray(parameters["envelope_times"]) : new Array<float>();
            if (times.Count > 0)
            {
                if (!TimesValid(times, envelope, out error))
                {
                    error = "envelope_" + error;
                    return null;
                }
                instrument.EnvelopeTimes = times;
            }

            var pitch = parameters.ContainsKey("pitch_envelope") ? FloatArray(parameters["pitch_envelope"]) : new Array<float>();
            instrument.PitchEnvelope = pitch;
            var pitchTimes = parameters.ContainsKey("pitch_envelope_times") ? FloatArray(parameters["pitch_envelope_times"]) : new Array<float>();
            if (pitchTimes.Count > 0)
            {
                if (!TimesValid(pitchTimes, pitch, out error))
                {
                    error = "pitch_envelope_" + error;
                    return null;
                }
                instrument.PitchEnvelopeTimes = pitchTimes;
            }
            if (parameters.ContainsKey("pitch_range"))
                instrument.PitchRange = parameters["pitch_range"].AsInt32();

            if (allowLayers && parameters.ContainsKey("layers"))
            {
                foreach (var layerVariant in parameters["layers"].AsGodotArray())
                {
                    var layerParams = layerVariant.AsGodotDictionary();
                    var layer = BuildInstrument(layerParams, false, out error);
                    if (layer == null)
                        return null;
                    layer.StepOffset = layerParams.ContainsKey("step_offset") ? layerParams["step_offset"].AsInt32() : 0;
                    layer.FixedNoteEnabled = layerParams.ContainsKey("fixed_note_enabled") && layerParams["fixed_note_enabled"].AsBool();
                    layer.FixedNote = layerParams.ContainsKey("fixed_note") ? layerParams["fixed_note"].AsInt32() : 60;
                    instrument.Layers.Add(layer);
                }
            }

            return instrument;
        }

        // Smallest instrument's array index whose id matches, or -1.
        // Instrument commands below take `id` (stable, from get_song/
        // add_instrument) rather than array position, since removing an
        // earlier instrument shifts every later one's position but never
        // its id.
        int FindInstrumentIndex(int id)
        {
            for (var i = 0; i < Song.Instruments.Count; i++)
            {
                if (Song.Instruments[i].Id == id)
                    return i;
            }
            return -1;
        }

        // Updates an existing instrument's own fields in place -- never
        // its layers (see AddLayer/UpdateLayer/RemoveLayer for those).
        // Only fields present in `parameters` are changed, the same
        // partial-update rule SetNote uses.
        Dictionary UpdateInstrument(Dictionary parameters)
        {
            if (!parameters.ContainsKey("id"))
                return Err("update_instrument requires id");
            var id = parameters["id"].AsInt32();
            var index = FindInstrumentIndex(id);
            if (index == -1)
                return Err($"no instrument with id {id}");
            var instrument = Song.Instruments[index];
            if (Song.IsMetronomeInstrument(instrument))
                return Err("the Metronome instrument can't be edited; it's managed from the dock");
            if (parameters.ContainsKey("waveform"))
                instrument.Waveform = parameters["waveform"].AsString();
            if (parameters.ContainsKey("duty_cycle"))
                instrument.DutyCycle = parameters["duty_cycle"].AsSingle();
            if (parameters.ContainsKey("envelope"))
            {
                var envelope = FloatArray(parameters["envelope"]);
                var times = parameters.ContainsKey("envelope_times") ? FloatArray(parameters["envelope_times"]) : new Array<float>();
                if (times.Count > 0 && !TimesValid(times, envelope, out var timesError))
                    return Err("envelope_" + timesError);
                instrument.Envelope = envelope;
                instrument.EnvelopeTimes = times;
            }
            if (parameters.ContainsKey("pitch_envelope"))
            {
                var pitch = FloatArray(parameters["pitch_envelope"]);
                var pitchTimes = parameters.ContainsKey("pitch_envelope_times") ? FloatArray(parameters["pitch_envelope_times"]) : new Array<float>();
                if (pitchTimes.Count > 0 && !TimesValid(pitchTimes, pitch, out var pitchTimesError))
                    return Err("pitch_envelope_" + pitchTimesError);
                instrument.PitchEnvelope = pitch;
                instrument.PitchEnvelopeTimes = pitchTimes;
            }
            if (parameters.ContainsKey("pitch_range"))
                instrument.PitchRange = parameters["pitch_range"].AsInt32();
            NotifyMutated(false);
            return Ok(new Dictionary());
        }

        Dictionary RenameInstrument(Dictionary parameters)
        {
            if (!(parameters.ContainsKey("id") && parameters.ContainsKey("name")))
                return Err("rename_instrument requires id and name");
            var index = FindInstrumentIndex(parameters["id"].AsInt32());
            if (index == -1)
                return Err($"no instrument with id {parameters["id"].AsInt32()}");
            var instrument = Song.Instruments[index];
            if (Song.IsMetronomeInstrument(instrument))
                return Err("the Metronome instrument can't be renamed");
            var newName = parameters["name"].AsString().Trim();
            if (Song.IsReservedName(newName))
                return Err($"'{newName}' is reserved for the metronome");
            instrument.Name = newName;
            NotifyMutated(false);
            return Ok(new Dictionary());
        }

        Dictionary RemoveInstrument(Dictionary parameters)
        {
            if (!parameters.ContainsKey("id"))
                return Err("remove_instrument requires id");
            var index = FindInstrumentIndex(parameters["id"].AsInt32());
            if (index == -1)
                return Err($"no instrument with id {parameters["id"].AsInt32()}");
            if (Song.IsMetronomeInstrument(Song.Instruments[index]))
                return Err("the Metronome instrument can't be removed; the metronome is managed from the dock");
            Song.Instruments.RemoveAt(index);
            NotifyMutated(false);
            return Ok(new Dictionary());
        }

        // Deep-copies an instrument, layers and all, as a new instrument
        // with a fresh id appended to the end of the list.
        Dictionary DuplicateInstrument(Dictionary parameters)
        {
            if (!parameters.ContainsKey("id"))
                return Err("duplicate_instrument requires id");
            var index = FindInstrumentIndex(parameters["id"].AsInt32());
            if (index == -1)
                return Err($"no instrument with id {parameters["id"].AsInt32()}");
            var copy = (Instrument)Song.Instruments[index].Duplicate(true);
            copy.Id = Song.NextInstrumentId();
            if (!string.IsNullOrEmpty(copy.Name))
                copy.Name += " (Copy)";
            Song.Instruments.Add(copy);
            NotifyMutated(false);
            return Ok(new Dictionary { ["instrument_id"] = copy.Id });
        }

        // Adds a layer to an existing instrument -- same fields
        // BuildInstrument takes for a top-level instrument, plus
        // step_offset/fixed_note_enabled/fixed_note. Never allowed on the
        // Metronome instrument; a layer can never itself have layers.
        Dictionary AddLayer(Dictionary parameters)
        {
            if (!parameters.ContainsKey("instrument_id"))
                return Err("add_layer requires instrument_id");
            var index = FindInstrumentIndex(parameters["instrument_id"].AsInt32());
            if (index == -1)
                return Err($"no instrument with id {parameters["instrument_id"].AsInt32()}");
            var instrument = Song.Instruments[index];
            if (Song.IsMetronomeInstrument(instrument))
                return Err("the Metronome instrument can't have layers");
            var layer = BuildInstrument(parameters, false, out var error);
            if (layer == null)
                return Err(error);
            layer.StepOffset = parameters.ContainsKey("step_offset") ? parameters["step_offset"].AsInt32() : 0;
            layer.FixedNoteEnabled = parameters.ContainsKey("fixed_note_enabled") && parameters["fixed_note_enabled"].AsBool();
            layer.FixedNote = parameters.ContainsKey("fixed_note") ? parameters["fixed_note"].AsInt32() : 60;
            instrument.Layers.Add(layer);
            NotifyMutated(false);
            return Ok(new Dictionary { ["layer_index"] = instrument.Layers.Count - 1 });
        }

        // Field-by-field update of an existing layer -- same partial-
        // update rule as UpdateInstrument, plus
        // step_offset/fixed_note_enabled/fixed_note.
        Dictionary UpdateLayer(Dictionary parameters)
        {
            if (!(parameters.ContainsKey("instrument_id") && parameters.ContainsKey("layer_index")))
                return Err("update_layer requires instrument_id and layer_index");
            var index = FindInstrumentIndex(parameters["instrument_id"].AsInt32());
            if (index == -1)
                return Err($"no instrument with id {parameters["instrument_id"].AsInt32()}");
            var instrument = Song.Instruments[index];
            var layerIndex = parameters["layer_index"].AsInt32();
            if (layerIndex < 0 || layerIndex >= instrument.Layers.Count)
                return Err("layer index out of range");
            var layer = instrument.Layers[layerIndex];
            if (parameters.ContainsKey("waveform"))
                layer.Waveform = parameters["waveform"].AsString();
            if (parameters.ContainsKey("duty_cycle"))
                layer.DutyCycle = parameters["duty_cycle"].AsSingle();
            if (parameters.ContainsKey("envelope"))
            {
                var envelope = FloatArray(parameters["envelope"]);
                var times = parameters.ContainsKey("envelope_times") ? FloatArray(parameters["envelope_times"]) : new Array<float>();
                if (times.Count > 0 && times.Count != envelope.Count)
                    return Err("envelope_times must have one entry per envelope point");
                layer.Envelope = envelope;
                layer.EnvelopeTimes = times;
            }
            if (parameters.ContainsKey("pitch_envelope"))
            {
                var pitch = FloatArray(parameters["pitch_envelope"]);
                var pitchTimes = parameters.ContainsKey("pitch_envelope_times") ? FloatArray(parameters["pitch_envelope_times"]) : new Array<float>();
                if (pitchTimes.Count > 0 && pitchTimes.Count != pitch.Count)
                    return Err("pitch_envelope_times must have one entry per pitch envelope point");
                layer.PitchEnvelope = pitch;
                layer.PitchEnvelopeTimes = pitchTimes;
            }
            if (parameters.ContainsKey("pitch_range"))
                layer.PitchRange = parameters["pitch_range"].AsInt32();
            if (parameters.ContainsKey("step_offset"))
                layer.StepOffset = parameters["step_offset"].AsInt32();
            if (parameters.ContainsKey("fixed_note_enabled"))
                layer.FixedNoteEnabled = parameters["fixed_note_enabled"].AsBool();
            if (parameters.ContainsKey("fixed_note"))
                layer.FixedNote = parameters["fixed_note"].AsInt32();
            NotifyMutated(false);
            return Ok(new Dictionary());
        }

        Dictionary RemoveLayer(Dictionary parameters)
        {
            if (!(parameters.ContainsKey("instrument_id") && parameters.ContainsKey("layer_index")))
                return Err("remove_layer requires instrument_id and layer_index");
            var index = FindInstrumentIndex(parameters["instrument_id"].AsInt32());
            if (index == -1)
                return Err($"no instrument with id {parameters["instrument_id"].AsInt32()}");
            var instrument = Song.Instruments[index];
            var layerIndex = parameters["layer_index"].AsInt32();
            if (layerIndex < 0 || layerIndex >= instrument.Layers.Count)
                return Err("layer index out of range");
            instrument.Layers.RemoveAt(layerIndex);
            NotifyMutated(false);
            return Ok(new Dictionary());
        }

        Dictionary ListArchetypes()
        {
            var archetypes = new Godot.Collections.Array();
            for (var i = 0; i < EnvelopeArchetypes.Count(); i++)
            {
                archetypes.Add(new Dictionary
                {
                    ["index"] = i,
                    ["name"] = EnvelopeArchetypes.ArchetypeName(i),
                    ["waveform"] = EnvelopeArchetypes.DefaultWaveform(i),
                    ["has_pitch"] = EnvelopeArchetypes.HasPitch(i),
                });
            }
            return Ok(new Dictionary { ["archetypes"] = archetypes, ["max_nodes"] = EnvelopeArchetypes.MaxNodes });
        }

        // Adds an instrument whose envelope is a fresh random variation of
        // a named archetype (the same generator as the instrument panel's
        // RANDOMIZE). For an archetype with a pitch curve
        // (ListArchetypes' has_pitch), a matching pitch envelope is
        // generated too, with its own node count and clamped to
        // +/-pitch_range semitones; an archetype with none leaves pitch flat.
        Dictionary RandomInstrument(Dictionary parameters)
        {
            var archetypeQuery = parameters.ContainsKey("archetype") ? parameters["archetype"].AsString() : "";
            var index = EnvelopeArchetypes.Find(archetypeQuery);
            if (index == -1)
                return Err($"unknown archetype '{archetypeQuery}'; list_archetypes shows the names");
            var nodes = Mathf.Clamp(parameters.ContainsKey("node_count") ? parameters["node_count"].AsInt32() : 8, EnvelopeArchetypes.MinNodes, EnvelopeArchetypes.MaxNodes);
            var pitchNodes = Mathf.Clamp(parameters.ContainsKey("pitch_node_count") ? parameters["pitch_node_count"].AsInt32() : nodes, EnvelopeArchetypes.MinNodes, EnvelopeArchetypes.MaxNodes);
            var pitchRange = parameters.ContainsKey("pitch_range") ? parameters["pitch_range"].AsInt32() : 24;
            var rng = new RandomNumberGenerator();
            if (parameters.ContainsKey("seed"))
                rng.Seed = (ulong)parameters["seed"].AsInt64();
            else
                rng.Randomize();
            var generated = EnvelopeArchetypes.Generate(index, nodes, rng);
            var generatedPitch = EnvelopeArchetypes.GeneratePitch(index, pitchNodes, pitchRange, rng);
            var instrument = new Instrument
            {
                Id = Song.NextInstrumentId(),
                Waveform = parameters.ContainsKey("waveform") ? parameters["waveform"].AsString() : EnvelopeArchetypes.DefaultWaveform(index),
            };
            if (parameters.ContainsKey("duty_cycle"))
                instrument.DutyCycle = parameters["duty_cycle"].AsSingle();
            instrument.Name = parameters.ContainsKey("name") ? parameters["name"].AsString() : EnvelopeArchetypes.ArchetypeName(index).Split(": ", 2)[^1];
            instrument.Envelope = generated.Levels;
            instrument.EnvelopeTimes = generated.Times;
            instrument.PitchEnvelope = generatedPitch.Levels;
            instrument.PitchEnvelopeTimes = generatedPitch.Times;
            instrument.PitchRange = pitchRange;
            Song.Instruments.Add(instrument);
            NotifyMutated(false);

            var envelopeOut = new Godot.Collections.Array();
            foreach (var v in generated.Levels)
                envelopeOut.Add(v);
            var envelopeTimesOut = new Godot.Collections.Array();
            foreach (var v in generated.Times)
                envelopeTimesOut.Add(v);
            var pitchEnvelopeOut = new Godot.Collections.Array();
            foreach (var v in generatedPitch.Levels)
                pitchEnvelopeOut.Add(v);
            var pitchTimesOut = new Godot.Collections.Array();
            foreach (var v in generatedPitch.Times)
                pitchTimesOut.Add(v);
            return Ok(new Dictionary
            {
                ["instrument_id"] = instrument.Id,
                ["archetype"] = EnvelopeArchetypes.ArchetypeName(index),
                ["waveform"] = instrument.Waveform,
                ["envelope"] = envelopeOut,
                ["envelope_times"] = envelopeTimesOut,
                ["pitch_envelope"] = pitchEnvelopeOut,
                ["pitch_envelope_times"] = pitchTimesOut,
            });
        }

        Dictionary AddChannel(Dictionary parameters)
        {
            var name = parameters.ContainsKey("name") ? parameters["name"].AsString() : $"Ch {Song.Channels.Count}";
            if (Song.IsReservedName(name))
                return Err($"the name '{name}' is reserved for the metronome");
            var channel = new Channel
            {
                Name = name,
                InstrumentType = parameters.ContainsKey("instrument_type") ? parameters["instrument_type"].AsString() : "square",
            };
            Song.AddChannel(channel);
            NotifyMutated(false);
            return Ok(new Dictionary { ["channel_index"] = Song.Channels.Count - 1 });
        }

        Dictionary RemoveChannel(Dictionary parameters)
        {
            if (!parameters.ContainsKey("index"))
                return Err("remove_channel requires index");
            var index = parameters["index"].AsInt32();
            if (Song.Channels.Count <= 1)
                return Err("cannot remove the last remaining channel");
            if (index < 0 || index >= Song.Channels.Count)
                return Err("channel index out of range");
            if (Song.IsMetronomeChannel(index))
                return Err("the Metronome channel can't be removed; the metronome is managed from the dock");
            Song.RemoveChannel(index);
            NotifyMutated(false);
            return Ok(new Dictionary());
        }

        Dictionary RenameChannel(Dictionary parameters)
        {
            if (!(parameters.ContainsKey("index") && parameters.ContainsKey("name")))
                return Err("rename_channel requires index and name");
            var index = parameters["index"].AsInt32();
            if (index < 0 || index >= Song.Channels.Count)
                return Err("channel index out of range");
            if (Song.IsMetronomeChannel(index))
                return Err("the Metronome channel can't be renamed");
            var newName = parameters["name"].AsString().Trim();
            if (Song.IsReservedName(newName))
                return Err($"'{newName}' is reserved for the metronome");
            Song.Channels[index].Name = newName;
            NotifyMutated(false);
            return Ok(new Dictionary());
        }

        // Sets muted/solo and/or instrument_type on a channel, whichever
        // of these are present in `parameters` (the same partial-update
        // rule SetNote uses). The Metronome channel's mute/solo can still
        // be changed -- muting it doesn't remove it -- but its
        // instrument_type is fixed.
        Dictionary UpdateChannel(Dictionary parameters)
        {
            if (!parameters.ContainsKey("index"))
                return Err("update_channel requires index");
            var index = parameters["index"].AsInt32();
            if (index < 0 || index >= Song.Channels.Count)
                return Err("channel index out of range");
            var channel = Song.Channels[index];
            if (parameters.ContainsKey("muted"))
                channel.Muted = parameters["muted"].AsBool();
            if (parameters.ContainsKey("solo"))
                channel.Solo = parameters["solo"].AsBool();
            if (parameters.ContainsKey("instrument_type"))
            {
                if (Song.IsMetronomeChannel(index))
                    return Err("the Metronome channel's instrument type can't be changed");
                channel.InstrumentType = parameters["instrument_type"].AsString();
            }
            NotifyMutated(false);
            return Ok(new Dictionary());
        }

        // Reassigns a channel to a different group -- an absolute target
        // group, unlike the dock's <-/-> buttons which only move one step
        // at a time.
        Dictionary MoveChannelToGroup(Dictionary parameters)
        {
            if (!(parameters.ContainsKey("channel_index") && parameters.ContainsKey("group_index")))
                return Err("move_channel_to_group requires channel_index and group_index");
            var channelIndex = parameters["channel_index"].AsInt32();
            var groupIndex = parameters["group_index"].AsInt32();
            if (channelIndex < 0 || channelIndex >= Song.Channels.Count)
                return Err("channel index out of range");
            if (groupIndex < 0 || groupIndex >= Song.Groups.Count)
                return Err("group index out of range");
            if (Song.IsMetronomeChannel(channelIndex) || Song.IsMetronomeGroup(groupIndex))
                return Err("the Metronome channel/group can't be reassigned");
            Song.Channels[channelIndex].GroupIndex = groupIndex;
            NotifyMutated(false);
            return Ok(new Dictionary());
        }

        Dictionary AddGroup()
        {
            var index = Song.AddGroup();
            NotifyMutated(false);
            return Ok(new Dictionary { ["group_index"] = index });
        }

        Dictionary RemoveGroup(Dictionary parameters)
        {
            if (!parameters.ContainsKey("index"))
                return Err("remove_group requires index");
            var index = parameters["index"].AsInt32();
            if (Song.Groups.Count <= 1)
                return Err("cannot remove the last remaining group");
            if (index <= 0)
                return Err("Group 1 (index 0) can't be removed");
            if (index >= Song.Groups.Count)
                return Err("group index out of range");
            if (Song.IsMetronomeGroup(index))
                return Err("the Metronome group can't be removed; the metronome is managed from the dock");
            Song.RemoveGroup(index);
            NotifyMutated(false);
            return Ok(new Dictionary());
        }

        Dictionary RenameGroup(Dictionary parameters)
        {
            if (!(parameters.ContainsKey("index") && parameters.ContainsKey("name")))
                return Err("rename_group requires index and name");
            var index = parameters["index"].AsInt32();
            if (index < 0 || index >= Song.Groups.Count)
                return Err("group index out of range");
            if (Song.IsMetronomeGroup(index))
                return Err("the Metronome group can't be renamed");
            var newName = parameters["name"].AsString().Trim();
            if (Song.IsReservedName(newName))
                return Err($"'{newName}' is reserved for the metronome");
            Song.Groups[index].Name = newName;
            NotifyMutated(false);
            return Ok(new Dictionary());
        }

        Dictionary AddMetronome()
        {
            if (!Song.AddMetronome())
                return Err("the song already has a metronome");
            NotifyMutated(false);
            return Ok(new Dictionary
            {
                ["channel_index"] = Song.MetronomeChannelIndex(),
                ["instrument_id"] = Song.MetronomeInstrument().Id,
            });
        }

        Dictionary RemoveMetronome()
        {
            if (!Song.RemoveMetronome())
                return Err("no metronome to remove, or it's the song's only channel");
            NotifyMutated(false);
            return Ok(new Dictionary());
        }

        Dictionary AddPattern()
        {
            var index = Song.AddPattern();
            NotifyMutated(false);
            return Ok(new Dictionary { ["pattern_index"] = index });
        }

        Dictionary RemovePattern(Dictionary parameters)
        {
            if (!parameters.ContainsKey("index"))
                return Err("remove_pattern requires index");
            var index = parameters["index"].AsInt32();
            if (Song.Patterns.Count <= 1)
                return Err("cannot remove the last remaining pattern");
            if (index < 0 || index >= Song.Patterns.Count)
                return Err("pattern index out of range");
            Song.RemovePattern(index);
            NotifyMutated(false);
            return Ok(new Dictionary());
        }

        Dictionary RenamePattern(Dictionary parameters)
        {
            if (!(parameters.ContainsKey("index") && parameters.ContainsKey("name")))
                return Err("rename_pattern requires index and name");
            var index = parameters["index"].AsInt32();
            if (index < 0 || index >= Song.Patterns.Count)
                return Err("pattern index out of range");
            Song.Patterns[index].Name = parameters["name"].AsString();
            NotifyMutated(false);
            return Ok(new Dictionary());
        }

        Dictionary AddToOrder(Dictionary parameters)
        {
            if (!parameters.ContainsKey("pattern"))
                return Err("add_to_order requires pattern");
            var patternIndex = parameters["pattern"].AsInt32();
            if (patternIndex < 0 || patternIndex >= Song.Patterns.Count)
                return Err("pattern index out of range");
            Song.OrderList.Add(patternIndex);
            NotifyMutated(false);
            return Ok(new Dictionary { ["order_index"] = Song.OrderList.Count - 1 });
        }

        Dictionary RemoveFromOrder(Dictionary parameters)
        {
            if (!parameters.ContainsKey("order_index"))
                return Err("remove_from_order requires order_index");
            var index = parameters["order_index"].AsInt32();
            if (index < 0 || index >= Song.OrderList.Count)
                return Err("order index out of range");
            Song.OrderList.RemoveAt(index);
            NotifyMutated(false);
            return Ok(new Dictionary());
        }

        // Swaps the order-list entry at `order_index` with its neighbour
        // in `direction` (-1 earlier, 1 later) -- the same swap the
        // dock's move up/down buttons do, not a reorder to an arbitrary
        // position.
        Dictionary MoveOrderEntry(Dictionary parameters)
        {
            if (!(parameters.ContainsKey("order_index") && parameters.ContainsKey("direction")))
                return Err("move_order_entry requires order_index and direction");
            var index = parameters["order_index"].AsInt32();
            var direction = parameters["direction"].AsInt32();
            if (direction != -1 && direction != 1)
                return Err("direction must be -1 or 1");
            var target = index + direction;
            if (index < 0 || index >= Song.OrderList.Count || target < 0 || target >= Song.OrderList.Count)
                return Err("order index out of range");
            (Song.OrderList[index], Song.OrderList[target]) = (Song.OrderList[target], Song.OrderList[index]);
            NotifyMutated(false);
            return Ok(new Dictionary());
        }

        Dictionary SetNote(Dictionary parameters)
        {
            if (!(parameters.ContainsKey("pattern") && parameters.ContainsKey("row") && parameters.ContainsKey("channel")))
                return Err("set_note requires pattern, row, channel");
            var patternIndex = parameters["pattern"].AsInt32();
            var row = parameters["row"].AsInt32();
            var channel = parameters["channel"].AsInt32();

            if (patternIndex < 0)
                return Err("pattern index out of range");
            // Auto-grow: a pattern index one past the end creates a fresh
            // pattern and appends it to the order list, so a caller can
            // build a song up incrementally without a separate
            // "add_pattern" command.
            while (patternIndex >= Song.Patterns.Count)
            {
                Song.Patterns.Add(new Pattern(Song.RowsPerPattern, Song.Channels.Count));
                Song.OrderList.Add(Song.Patterns.Count - 1);
            }

            var pattern = Song.Patterns[patternIndex];
            if (row < 0 || row >= pattern.Rows.Count)
                return Err("row out of range");
            if (channel < 0 || channel >= Song.Channels.Count)
                return Err("channel out of range");

            var cell = pattern.Rows[row][channel].As<Cell>();
            cell.Note = parameters.ContainsKey("note") ? parameters["note"].AsInt32() : cell.Note;
            cell.InstrumentId = parameters.ContainsKey("instrument_id") ? parameters["instrument_id"].AsInt32() : cell.InstrumentId;
            cell.Volume = parameters.ContainsKey("volume") ? parameters["volume"].AsInt32() : cell.Volume;
            NotifyMutated(false);
            return Ok(new Dictionary());
        }

        Dictionary SetTempo(Dictionary parameters)
        {
            if (!parameters.ContainsKey("tempo"))
                return Err("set_tempo requires tempo");
            Song.Tempo = parameters["tempo"].AsInt32();
            if (parameters.ContainsKey("rows_per_beat"))
                Song.SetRowsPerBeat(parameters["rows_per_beat"].AsInt32());
            NotifyMutated(false);
            return Ok(new Dictionary());
        }

        // Without `pattern`, this resizes every pattern and sets the
        // song's default length for new ones -- the original, deliberately
        // song-wide behavior. With `pattern`, it resizes just that one and
        // leaves the default alone, matching what the Rows field in the
        // transport bar does (patterns are independently sized; see
        // Song.SetPatternRowCount).
        Dictionary SetRowsPerPattern(Dictionary parameters)
        {
            if (!parameters.ContainsKey("rows_per_pattern"))
                return Err("set_rows_per_pattern requires rows_per_pattern");
            var newCount = parameters["rows_per_pattern"].AsInt32();
            if (newCount < 1)
                return Err("rows_per_pattern must be at least 1");
            if (parameters.ContainsKey("pattern"))
            {
                var patternIndex = parameters["pattern"].AsInt32();
                if (patternIndex < 0 || patternIndex >= Song.Patterns.Count)
                    return Err($"pattern {patternIndex} is out of range");
                Song.SetPatternRowCount(patternIndex, newCount);
            }
            else
            {
                Song.SetRowsPerPattern(newCount);
            }
            NotifyMutated(false);
            return Ok(new Dictionary());
        }

        // Resolves `pattern` to its position in the order list and plays
        // the song from there (looping), rather than isolating just that
        // one pattern -- the engine has no "loop a single pattern outside
        // the order list" mode.
        Dictionary PlayPreview(Dictionary parameters)
        {
            if (Song.OrderList.Count == 0)
                return Err("song has no patterns to play");
            var patternIndex = parameters.ContainsKey("pattern") ? parameters["pattern"].AsInt32() : Song.OrderList[0];
            var row = parameters.ContainsKey("row") ? parameters["row"].AsInt32() : 0;
            var orderIndex = Song.OrderList.IndexOf(patternIndex);
            if (orderIndex == -1)
                return Err($"pattern {patternIndex} is not in the order list");

            // loop=true means a preview a caller forgets to stop_preview
            // keeps live-synthesizing forever in the background --
            // stopping whatever's already playing before starting the new
            // one means a second play_preview call can't stack playback
            // or leave an orphaned loop running once this one replaces it.
            _previewEngine.Stop();
            _previewEngine.Loop = true;
            _previewEngine.Setup(Song, _previewPlayer);
            _previewEngine.Play(orderIndex, row);
            return Ok(new Dictionary());
        }

        Dictionary StopPreview()
        {
            _previewEngine.Stop();
            return Ok(new Dictionary());
        }

        Dictionary RenderWav(Dictionary parameters)
        {
            if (!parameters.ContainsKey("output_path"))
                return Err("render_wav requires output_path");
            var outputPath = parameters["output_path"].AsString();
            var err = WavRenderer.RenderSong(Song, outputPath);
            if (err != Error.Ok)
                return Err($"render failed with error code {err}");
            return Ok(new Dictionary { ["path"] = outputPath });
        }

        // An instrument's fields as get_song reports them, minus id/name
        // (added by the caller for a top-level instrument -- a layer has
        // neither). Includes "layers" as a nested array of these same
        // dicts, one level deep, since that's all layering ever goes.
        static Dictionary InstrumentToDict(Instrument instrument)
        {
            var layersData = new Godot.Collections.Array();
            foreach (var layer in instrument.Layers)
            {
                var layerData = InstrumentToDict(layer);
                layerData["step_offset"] = layer.StepOffset;
                layerData["fixed_note_enabled"] = layer.FixedNoteEnabled;
                layerData["fixed_note"] = layer.FixedNote;
                layersData.Add(layerData);
            }
            var envelopeTimes = new Godot.Collections.Array();
            foreach (var t in instrument.ResolvedTimes())
                envelopeTimes.Add(t);
            var pitchTimes = new Godot.Collections.Array();
            foreach (var t in instrument.ResolvedPitchTimes())
                pitchTimes.Add(t);
            var envelope = new Godot.Collections.Array();
            foreach (var v in instrument.Envelope)
                envelope.Add(v);
            var pitchEnvelope = new Godot.Collections.Array();
            foreach (var v in instrument.PitchEnvelope)
                pitchEnvelope.Add(v);
            return new Dictionary
            {
                ["waveform"] = instrument.Waveform,
                ["duty_cycle"] = instrument.DutyCycle,
                ["envelope"] = envelope,
                ["envelope_times"] = envelopeTimes,
                ["pitch_envelope"] = pitchEnvelope,
                ["pitch_envelope_times"] = pitchTimes,
                ["pitch_range"] = instrument.PitchRange,
                ["layers"] = layersData,
            };
        }

        Dictionary SongToDict()
        {
            var channelsData = new Godot.Collections.Array();
            foreach (var channel in Song.Channels)
            {
                channelsData.Add(new Dictionary
                {
                    ["name"] = channel.Name,
                    ["instrument_type"] = channel.InstrumentType,
                    ["muted"] = channel.Muted,
                    ["solo"] = channel.Solo,
                    ["group_index"] = channel.GroupIndex,
                });
            }

            var groupsData = new Godot.Collections.Array();
            foreach (var group in Song.Groups)
                groupsData.Add(new Dictionary { ["name"] = group.Name });

            var instrumentsData = new Godot.Collections.Array();
            foreach (var instrument in Song.Instruments)
            {
                var data = InstrumentToDict(instrument);
                data["id"] = instrument.Id;
                data["name"] = instrument.Name;
                instrumentsData.Add(data);
            }

            var patternsData = new Godot.Collections.Array();
            foreach (var pattern in Song.Patterns)
            {
                var rowsData = new Godot.Collections.Array();
                foreach (var row in pattern.Rows)
                {
                    var rowData = new Godot.Collections.Array();
                    foreach (var cellVariant in row)
                    {
                        var cell = cellVariant.As<Cell>();
                        rowData.Add(new Dictionary
                        {
                            ["note"] = cell.Note,
                            ["instrument_id"] = cell.InstrumentId,
                            ["volume"] = cell.Volume,
                            ["effect"] = cell.Effect,
                            ["effect_param"] = cell.EffectParam,
                        });
                    }
                    rowsData.Add(rowData);
                }
                patternsData.Add(new Dictionary { ["name"] = pattern.Name, ["rows"] = rowsData });
            }

            var orderList = new Godot.Collections.Array();
            foreach (var o in Song.OrderList)
                orderList.Add(o);

            return new Dictionary
            {
                ["tempo"] = Song.Tempo,
                ["rows_per_beat"] = Song.RowsPerBeat,
                ["rows_per_pattern"] = Song.RowsPerPattern,
                ["channels"] = channelsData,
                ["groups"] = groupsData,
                ["order_list"] = orderList,
                ["patterns"] = patternsData,
                ["instruments"] = instrumentsData,
            };
        }
    }
}
