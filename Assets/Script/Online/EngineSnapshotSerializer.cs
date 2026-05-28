using System;
using LiteNetLib.Utils;
using YARG.Core.Engine;
using YARG.Core.Engine.Drums;
using YARG.Core.Engine.Guitar;
using YARG.Core.Engine.Keys;
using YARG.Core.Engine.Vocals;

namespace YARG.Online
{
    /// <summary>
    /// Wire format for an <see cref="EngineSnapshot"/>. Encodes / decodes the
    /// snapshot's fields field-by-field through a <see cref="NetDataWriter"/>
    /// / <see cref="NetDataReader"/> pair. The byte blob is opaque to the
    /// server -- only the sender and receiver agree on the layout.
    ///
    /// SnapshotKind discriminates subclass-specific extensions:
    ///   0 = invalid
    ///   1 = GuitarEngineSnapshot
    ///   2 = DrumsEngineSnapshot
    ///   3 = KeysEngineSnapshot
    ///   4 = VocalsEngineSnapshot
    ///
    /// To add an instrument: bump SnapshotKind, add subclass-specific
    /// field reads/writes after the shared block in the matching kind branch.
    /// </summary>
    public static class EngineSnapshotSerializer
    {
        public const byte KindGuitar = 1;
        public const byte KindDrums  = 2;
        public const byte KindKeys   = 3;
        public const byte KindVocals = 4;

        /// <summary>Pick the right SnapshotKind for a given snapshot
        /// instance. Throws if the subtype isn't supported.</summary>
        public static byte KindFor(EngineSnapshot snapshot)
        {
            return snapshot switch
            {
                GuitarEngineSnapshot _ => KindGuitar,
                DrumsEngineSnapshot  _ => KindDrums,
                KeysEngineSnapshot   _ => KindKeys,
                VocalsEngineSnapshot _ => KindVocals,
                _ => throw new NotSupportedException(
                    $"EngineSnapshotSerializer: unsupported snapshot subtype {snapshot.GetType().FullName}"),
            };
        }

        /// <summary>Serialize <paramref name="snapshot"/> into <paramref name="writer"/>
        /// at its current position. The caller owns the writer and the surrounding
        /// packet framing (opcode, header fields, and the length prefix that wraps
        /// these bytes) -- this method neither resets the writer nor copies out a
        /// buffer; it appends the snapshot's SnapshotData payload directly.</summary>
        public static void Serialize(NetDataWriter writer, EngineSnapshot snapshot)
        {
            if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));

            WriteBase(writer, snapshot);

            switch (snapshot)
            {
                case GuitarEngineSnapshot g:
                    WriteGuitar(writer, g);
                    break;
                case DrumsEngineSnapshot d:
                    WriteDrums(writer, d);
                    break;
                case KeysEngineSnapshot k:
                    WriteKeys(writer, k);
                    break;
                case VocalsEngineSnapshot v:
                    WriteVocals(writer, v);
                    break;
                default:
                    throw new NotSupportedException(
                        $"EngineSnapshotSerializer.Serialize: unsupported subtype {snapshot.GetType().FullName}");
            }
        }

        /// <summary>Deserialize a snapshot. <paramref name="kind"/> must match
        /// the byte tag on the wire packet (see <see cref="KindGuitar"/>).
        /// Returns a fresh snapshot instance ready for engine.RestoreSnapshot.</summary>
        public static EngineSnapshot Deserialize(byte[] data, byte kind)
        {
            if (data is null) throw new ArgumentNullException(nameof(data));

            var reader = new NetDataReader(data);
            switch (kind)
            {
                case KindGuitar:
                {
                    var snap = new GuitarEngineSnapshot();
                    ReadBase(reader, snap);
                    ReadGuitar(reader, snap);
                    return snap;
                }
                case KindDrums:
                {
                    var snap = new DrumsEngineSnapshot();
                    ReadBase(reader, snap);
                    ReadDrums(reader, snap);
                    return snap;
                }
                case KindKeys:
                {
                    var snap = new KeysEngineSnapshot();
                    ReadBase(reader, snap);
                    ReadKeys(reader, snap);
                    return snap;
                }
                case KindVocals:
                {
                    var snap = new VocalsEngineSnapshot();
                    ReadBase(reader, snap);
                    ReadVocals(reader, snap);
                    return snap;
                }
                default:
                    throw new NotSupportedException(
                        $"EngineSnapshotSerializer.Deserialize: unknown SnapshotKind {kind}");
            }
        }

        // ---- Base layer ----

        private static void WriteBase(NetDataWriter w, EngineSnapshot s)
        {
            w.Put(s.NoteIndex);
            w.Put(s.CurrentTime);
            w.Put(s.LastUpdateTime);
            w.Put(s.LastQueuedInputTime);

            w.Put(s.CurrentTick);
            w.Put(s.LastTick);
            w.Put(s.FirstWhammyTick);

            w.Put(s.CurrentSoloIndex);
            w.Put(s.CurrentStarIndex);
            w.Put(s.CurrentWaitCountdownIndex);
            w.Put(s.CurrentCodaIndex);

            w.Put(s.IsSoloActive);
            w.Put(s.IsCodaActive);
            w.Put(s.CodaHasStarted);
            w.Put(s.IsWaitCountdownActive);
            w.Put(s.IsStarPowerInputActive);

            w.Put(s.BaseScore);
            w.Put(s.BaseNoteScore);

            w.Put(s.WasSpSustainActive);
            w.Put(s.StarPowerTickPosition);
            w.Put(s.PreviousStarPowerTickPosition);
            w.Put(s.StarPowerTickActivationPosition);
            w.Put(s.StarPowerTickEndPosition);
            w.Put(s.StarPowerActivationTime);
            w.Put(s.StarPowerEndTime);
            w.Put(s.BaseTimeInStarPower);

            w.Put(s.StarPowerWhammyTimerActive);
            w.Put(s.StarPowerWhammyTimerStartTime);

            w.Put(s.TotalLanes);
            w.Put(s.CurrentLaneIndex);
            w.Put(s.RequiredLaneNote);
            w.Put(s.NextTrillNote);
            w.Put(s.LaneExpireTime);

            // PendingInputs / PendingUpdates are not serialized. They hold
            // queued engine work that hadn't been flushed yet at snapshot
            // time. Since we send snapshots at quiet moments (between events)
            // they're almost always empty; even when non-empty, dropping
            // them just means the receiver may need one additional tick of
            // engine.Update() to converge -- corrected by the next snapshot.
            // Excluding them keeps the wire format simple and small.

            w.Put(s.NoteFlagStartIndex);
            w.PutBytesWithLength(s.NoteFlags);

            w.Put(s.ActiveSustains.Length);
            for (int i = 0; i < s.ActiveSustains.Length; i++)
            {
                var ss = s.ActiveSustains[i];
                w.Put(ss.NoteIndex);
                w.Put(ss.BaseTick);
                w.Put(ss.BaseScore);
                w.Put(ss.HasFinishedScoring);
                w.Put(ss.IsLeniencyHeld);
                w.Put(ss.LeniencyDropTime);
            }

            w.Put(s.WhammyTicksRemainder);

            // Per-solo accumulators (NotesHit + SoloBonus). Length matches the
            // engine's Solos.Count on both sides (chart-derived). Encoded as a
            // single count followed by paired ints so a malformed snapshot fails
            // fast on the length read.
            w.Put(s.SoloNotesHit.Length);
            for (int i = 0; i < s.SoloNotesHit.Length; i++)
            {
                w.Put(s.SoloNotesHit[i]);
                w.Put(s.SoloBonus[i]);
            }

            // BaseStats subclass-typed. The shared block writes the common
            // fields; the Guitar layer adds GuitarStats fields after.
            WriteBaseStats(w, s.Stats ?? throw new InvalidOperationException(
                "EngineSnapshotSerializer: snapshot.Stats is null"));
        }

        private static void ReadBase(NetDataReader r, EngineSnapshot s)
        {
            s.NoteIndex = r.GetInt();
            s.CurrentTime = r.GetDouble();
            s.LastUpdateTime = r.GetDouble();
            s.LastQueuedInputTime = r.GetDouble();

            s.CurrentTick = r.GetUInt();
            s.LastTick = r.GetUInt();
            s.FirstWhammyTick = r.GetUInt();

            s.CurrentSoloIndex = r.GetInt();
            s.CurrentStarIndex = r.GetInt();
            s.CurrentWaitCountdownIndex = r.GetInt();
            s.CurrentCodaIndex = r.GetInt();

            s.IsSoloActive = r.GetBool();
            s.IsCodaActive = r.GetBool();
            s.CodaHasStarted = r.GetBool();
            s.IsWaitCountdownActive = r.GetBool();
            s.IsStarPowerInputActive = r.GetBool();

            s.BaseScore = r.GetInt();
            s.BaseNoteScore = r.GetInt();

            s.WasSpSustainActive = r.GetBool();
            s.StarPowerTickPosition = r.GetUInt();
            s.PreviousStarPowerTickPosition = r.GetUInt();
            s.StarPowerTickActivationPosition = r.GetUInt();
            s.StarPowerTickEndPosition = r.GetUInt();
            s.StarPowerActivationTime = r.GetDouble();
            s.StarPowerEndTime = r.GetDouble();
            s.BaseTimeInStarPower = r.GetDouble();

            s.StarPowerWhammyTimerActive = r.GetBool();
            s.StarPowerWhammyTimerStartTime = r.GetDouble();

            s.TotalLanes = r.GetUInt();
            s.CurrentLaneIndex = r.GetUInt();
            s.RequiredLaneNote = r.GetInt();
            s.NextTrillNote = r.GetInt();
            s.LaneExpireTime = r.GetDouble();

            // PendingInputs / PendingUpdates aren't on the wire -- see comment
            // in WriteBase. Leave the snapshot's defaults (empty arrays).

            s.NoteFlagStartIndex = r.GetInt();
            s.NoteFlags = r.GetBytesWithLength();

            int sustainCount = r.GetInt();
            s.ActiveSustains = new SustainSnapshot[sustainCount];
            for (int i = 0; i < sustainCount; i++)
            {
                int noteIndex = r.GetInt();
                uint baseTick = r.GetUInt();
                double baseScore = r.GetDouble();
                bool hasFinishedScoring = r.GetBool();
                bool isLeniencyHeld = r.GetBool();
                double leniencyDropTime = r.GetDouble();
                s.ActiveSustains[i] = new SustainSnapshot(
                    noteIndex, baseTick, baseScore,
                    hasFinishedScoring, isLeniencyHeld, leniencyDropTime);
            }

            s.WhammyTicksRemainder = r.GetDouble();

            // Per-solo accumulators -- paired (NotesHit, SoloBonus) entries.
            int soloCount = r.GetInt();
            s.SoloNotesHit = new int[soloCount];
            s.SoloBonus    = new int[soloCount];
            for (int i = 0; i < soloCount; i++)
            {
                s.SoloNotesHit[i] = r.GetInt();
                s.SoloBonus[i]    = r.GetInt();
            }

            // Stats are subclass-typed. Allocated by the Guitar layer
            // before calling ReadBaseStats.
        }

        // ---- BaseStats ----

        private static void WriteBaseStats(NetDataWriter w, BaseStats st)
        {
            w.Put(st.CommittedScore);
            w.Put(st.PendingScore);
            w.Put(st.NoteScore);
            w.Put(st.SustainScore);
            w.Put(st.MultiplierScore);
            w.Put(st.BandBonusScore);
            w.Put(st.Combo);
            w.Put(st.MaxCombo);
            w.Put(st.ScoreMultiplier);
            w.Put(st.BandMultiplier);
            w.Put(st.NotesHit);
            w.Put(st.LanedNotesHit);
            w.Put(st.TotalNotes);
            w.Put(st.TotalChords);
            w.Put(st.StarPowerTickAmount);
            w.Put(st.TotalStarPowerTicks);
            w.Put(st.TotalStarPowerBarsFilled);
            w.Put(st.StarPowerActivationCount);
            w.Put(st.TimeInStarPower);
            w.Put(st.StarPowerWhammyTicks);
            w.Put(st.IsStarPowerActive);
            w.Put(st.StarPowerPhrasesHit);
            w.Put(st.TotalStarPowerPhrases);
            w.Put(st.SoloBonuses);
            w.Put(st.MaxSoloBonusPoints);
            w.Put(st.CodaBonuses);
            w.Put(st.StarPowerScore);
            w.Put(st.Stars);

            // Hit-timing histogram. The receiver's mirror engine commits hits
            // at note.Time, so its self-computed offsets are always 0. We
            // serialize the sender's authoritative list so the score-screen
            // histogram and the "Average Offset" debug HUD show real player
            // timing. Wire cost scales with notes-hit-so-far (~8 bytes each).
            w.Put(st.GetTotalOffset());
            var offsetSamples = st.GetOffsetSamples();
            w.Put(offsetSamples.Count);
            for (int i = 0; i < offsetSamples.Count; i++)
            {
                w.Put(offsetSamples[i]);
            }
        }

        private static void ReadBaseStats(NetDataReader r, BaseStats st)
        {
            st.CommittedScore = r.GetInt();
            st.PendingScore = r.GetInt();
            st.NoteScore = r.GetInt();
            st.SustainScore = r.GetInt();
            st.MultiplierScore = r.GetInt();
            st.BandBonusScore = r.GetInt();
            st.Combo = r.GetInt();
            st.MaxCombo = r.GetInt();
            st.ScoreMultiplier = r.GetInt();
            st.BandMultiplier = r.GetInt();
            st.NotesHit = r.GetInt();
            st.LanedNotesHit = r.GetInt();
            st.TotalNotes = r.GetInt();
            st.TotalChords = r.GetInt();
            st.StarPowerTickAmount = r.GetUInt();
            st.TotalStarPowerTicks = r.GetUInt();
            st.TotalStarPowerBarsFilled = r.GetDouble();
            st.StarPowerActivationCount = r.GetInt();
            st.TimeInStarPower = r.GetDouble();
            st.StarPowerWhammyTicks = r.GetUInt();
            st.IsStarPowerActive = r.GetBool();
            st.StarPowerPhrasesHit = r.GetInt();
            st.TotalStarPowerPhrases = r.GetInt();
            st.SoloBonuses = r.GetInt();
            st.MaxSoloBonusPoints = r.GetInt();
            st.CodaBonuses = r.GetInt();
            st.StarPowerScore = r.GetInt();
            st.Stars = r.GetFloat();

            // Hit-timing histogram (see WriteBaseStats for rationale).
            double totalOffset = r.GetDouble();
            int sampleCount = r.GetInt();
            var samples = new double[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                samples[i] = r.GetDouble();
            }
            st.SetOffsetData(totalOffset, samples);
        }

        // ---- Guitar layer ----

        private static void WriteGuitar(NetDataWriter w, GuitarEngineSnapshot s)
        {
            w.Put(s.EffectiveButtonMask);
            w.Put(s.InputButtonMask);
            w.Put(s.LastButtonMask);
            w.Put(s.StandardButtonHeld);
            w.Put(s.HasFretted);
            w.Put(s.HasStrummed);
            w.Put(s.HasTapped);
            w.Put(s.HasWhammied);
            w.Put(s.IsFretPress);
            w.Put(s.WasNoteGhosted);
            w.Put(s.HopoLeniencyTimerActive);
            w.Put(s.HopoLeniencyTimerStartTime);
            w.Put(s.StrumLeniencyTimerActive);
            w.Put(s.StrumLeniencyTimerStartTime);
            w.Put(s.FrontEndExpireTime);

            // GuitarStats extends BaseStats with three extra ints.
            var gs = s.Stats as GuitarStats
                ?? throw new InvalidOperationException(
                    "EngineSnapshotSerializer.WriteGuitar: Stats is not GuitarStats");
            w.Put(gs.Overstrums);
            w.Put(gs.HoposStrummed);
            w.Put(gs.GhostInputs);
        }

        private static void ReadGuitar(NetDataReader r, GuitarEngineSnapshot s)
        {
            var gs = new GuitarStats();
            s.Stats = gs;
            ReadBaseStats(r, gs);

            s.EffectiveButtonMask = r.GetByte();
            s.InputButtonMask = r.GetUShort();
            s.LastButtonMask = r.GetByte();
            s.StandardButtonHeld = r.GetBool();
            s.HasFretted = r.GetBool();
            s.HasStrummed = r.GetBool();
            s.HasTapped = r.GetBool();
            s.HasWhammied = r.GetBool();
            s.IsFretPress = r.GetBool();
            s.WasNoteGhosted = r.GetBool();
            s.HopoLeniencyTimerActive = r.GetBool();
            s.HopoLeniencyTimerStartTime = r.GetDouble();
            s.StrumLeniencyTimerActive = r.GetBool();
            s.StrumLeniencyTimerStartTime = r.GetDouble();
            s.FrontEndExpireTime = r.GetDouble();

            gs.Overstrums = r.GetInt();
            gs.HoposStrummed = r.GetInt();
            gs.GhostInputs = r.GetInt();
        }

        // ---- Drums layer ----

        private static void WriteDrums(NetDataWriter w, DrumsEngineSnapshot s)
        {
            // DrumsEngineSnapshot adds no fields of its own -- drums have no
            // button-mask / leniency / sustain state that survives a tick.
            // Only DrumsStats-specific stats need to be appended.
            var ds = s.Stats as DrumsStats
                ?? throw new InvalidOperationException(
                    "EngineSnapshotSerializer.WriteDrums: Stats is not DrumsStats");
            w.Put(ds.Overhits);
            w.Put(ds.GhostsHit);
            w.Put(ds.TotalGhosts);
            w.Put(ds.AccentsHit);
            w.Put(ds.TotalAccents);
            w.Put(ds.DynamicsBonus);

            // OverhitsByAction is a Dictionary<int, int>. Write as (count,
            // (key,value) pairs). Usually short -- at most a few entries per
            // distinct pad that's been overhit.
            w.Put(ds.OverhitsByAction.Count);
            foreach (var kv in ds.OverhitsByAction)
            {
                w.Put(kv.Key);
                w.Put(kv.Value);
            }
        }

        private static void ReadDrums(NetDataReader r, DrumsEngineSnapshot s)
        {
            var ds = new DrumsStats();
            s.Stats = ds;
            ReadBaseStats(r, ds);

            ds.Overhits = r.GetInt();
            ds.GhostsHit = r.GetInt();
            ds.TotalGhosts = r.GetInt();
            ds.AccentsHit = r.GetInt();
            ds.TotalAccents = r.GetInt();
            ds.DynamicsBonus = r.GetInt();

            int overhitsByActionCount = r.GetInt();
            for (int i = 0; i < overhitsByActionCount; i++)
            {
                int action = r.GetInt();
                int count = r.GetInt();
                ds.OverhitsByAction[action] = count;
            }
        }

        // ---- Keys layer ----
        //
        // KeysEngineSnapshot covers both ProKeys and 5-Lane Keys. The
        // fat-finger fields are only populated by ProKeys; 5-Lane writes
        // their defaults (-1 / inactive) and reads them back as no-ops.

        private static void WriteKeys(NetDataWriter w, KeysEngineSnapshot s)
        {
            var ks = s.Stats as KeysStats
                ?? throw new InvalidOperationException(
                    "EngineSnapshotSerializer.WriteKeys: Stats is not KeysStats");
            w.Put(ks.Overhits);
            w.Put(ks.FatFingersIgnored);

            w.Put(s.KeyMask);
            w.Put(s.PreviousKeyMask);
            w.Put(s.ChordStaggerTimerActive);
            w.Put(s.ChordStaggerTimerStartTime);
            w.Put(s.FatFingerTimerActive);
            w.Put(s.FatFingerTimerStartTime);
            w.Put(s.FatFingerKey);
            w.Put(s.FatFingerNoteIndex);
        }

        private static void ReadKeys(NetDataReader r, KeysEngineSnapshot s)
        {
            var ks = new KeysStats();
            s.Stats = ks;
            ReadBaseStats(r, ks);

            ks.Overhits = r.GetInt();
            ks.FatFingersIgnored = r.GetInt();

            s.KeyMask = r.GetInt();
            s.PreviousKeyMask = r.GetInt();
            s.ChordStaggerTimerActive = r.GetBool();
            s.ChordStaggerTimerStartTime = r.GetDouble();
            s.FatFingerTimerActive = r.GetBool();
            s.FatFingerTimerStartTime = r.GetDouble();
            s.FatFingerKey = r.GetInt();
            s.FatFingerNoteIndex = r.GetInt();
        }

        // ---- Vocals layer ----
        //
        // VocalsStats adds TicksHit / TicksMissed / HasCarryNote. The
        // snapshot adds the in-progress phrase state (ticks total/hit/last
        // sing) plus a (phraseIndex, childIndex) pair locating the carried
        // note.

        private static void WriteVocals(NetDataWriter w, VocalsEngineSnapshot s)
        {
            var vs = s.Stats as VocalsStats
                ?? throw new InvalidOperationException(
                    "EngineSnapshotSerializer.WriteVocals: Stats is not VocalsStats");
            w.Put(vs.TicksHit);
            w.Put(vs.TicksMissed);
            w.Put(vs.HasCarryNote);

            w.Put(s.HasPhraseTicksTotal);
            w.Put(s.PhraseTicksTotal);
            w.Put(s.PhraseTicksHit);
            w.Put(s.LastSingTick);
            w.Put(s.PitchSang);
            w.Put(s.HasSang);
            w.Put(s.CarriedVocalNotePhraseIndex);
            w.Put(s.CarriedVocalNoteChildIndex);
        }

        private static void ReadVocals(NetDataReader r, VocalsEngineSnapshot s)
        {
            var vs = new VocalsStats();
            s.Stats = vs;
            ReadBaseStats(r, vs);

            vs.TicksHit = r.GetUInt();
            vs.TicksMissed = r.GetUInt();
            vs.HasCarryNote = r.GetBool();

            s.HasPhraseTicksTotal = r.GetBool();
            s.PhraseTicksTotal = r.GetUInt();
            s.PhraseTicksHit = r.GetDouble();
            s.LastSingTick = r.GetUInt();
            s.PitchSang = r.GetFloat();
            s.HasSang = r.GetBool();
            s.CarriedVocalNotePhraseIndex = r.GetInt();
            s.CarriedVocalNoteChildIndex = r.GetInt();
        }
    }
}
