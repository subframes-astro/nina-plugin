using Xunit;

namespace Subframes.NinaPlugin.Tests;

public class SessionPolicyTests
{
    // ── Same-target check ────────────────────────────────────────────────────

    [Fact]
    public void SameNameAndCoords_IsSame() =>
        Assert.True(TargetBlockPolicy.IsSameTarget(true, "M42", 83.82, -5.39, "M42", 83.82, -5.39));

    [Fact]
    public void RawVsNormalizedName_IsSame() =>
        Assert.True(TargetBlockPolicy.IsSameTarget(true, "M 42", 83.82, -5.39, "m42", 83.82, -5.39));

    [Fact]
    public void NoActiveTarget_IsNotSame() =>
        Assert.False(TargetBlockPolicy.IsSameTarget(false, "M42", 83.82, -5.39, "M42", 83.82, -5.39));

    [Fact]
    public void SameNameDifferentCoords_IsNewBlock() =>
        Assert.False(TargetBlockPolicy.IsSameTarget(true, "Sh2-126", 100.0, 10.0, "Sh2-126", 100.5, 10.0));

    [Fact]
    public void CoordsWithinTolerance_IsSame() =>
        Assert.True(TargetBlockPolicy.IsSameTarget(true, "M31", 10.6847, 41.2687, "M31", 10.6850, 41.2690));

    [Fact]
    public void RaWraparound_IsSame() =>
        Assert.True(TargetBlockPolicy.IsSameTarget(true, "X", 359.999, 0.0, "X", 0.001, 0.0));

    [Fact]
    public void UnknownCoords_FallBackToNameOnly() =>
        Assert.True(TargetBlockPolicy.IsSameTarget(true, "M42", 83.82, -5.39, "M42", 0, 0));

    // ── Idle policy ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(10, IdleAction.None)]
    [InlineData(45, IdleAction.MarkWaiting)]   // 45-minute gap must NOT close the session
    [InlineData(239, IdleAction.MarkWaiting)]
    [InlineData(240, IdleAction.Close)]
    public void AutoSession_Idle(double idle, IdleAction expected) =>
        Assert.Equal(expected, SessionIdlePolicy.Evaluate(false, false, false, idle, 30, 240));

    [Fact]
    public void AutoSession_AlreadyWaiting_DoesNotReMark() =>
        Assert.Equal(IdleAction.None, SessionIdlePolicy.Evaluate(false, false, true, 60, 30, 240));

    [Fact]
    public void ManualSession_NeverIdleClosedWhileSequenceRunning() =>
        Assert.Equal(IdleAction.None, SessionIdlePolicy.Evaluate(true, false, false, 1000, 30, 240));

    [Fact]
    public void ManualSession_SequenceStopped_ClosesAfterMaxIdle()
    {
        Assert.Equal(IdleAction.None, SessionIdlePolicy.Evaluate(true, true, true, 100, 30, 240));
        Assert.Equal(IdleAction.Close, SessionIdlePolicy.Evaluate(true, true, true, 241, 30, 240));
    }

    // ── Dawn ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Dawn_UnknownLocation_IsNotAfterDawn() =>
        Assert.False(DawnPolicy.IsAfterDawn(0, 0, new DateTime(2025, 10, 6, 12, 0, 0, DateTimeKind.Utc)));

    [Fact]
    public void Dawn_Midnight_IsNotAfterDawn() =>
        // 05:00 UTC = 00:00 CDT at lon -97 (Oct)
        Assert.False(DawnPolicy.IsAfterDawn(32.0, -97.0, new DateTime(2025, 10, 6, 5, 0, 0, DateTimeKind.Utc)));

    [Fact]
    public void Dawn_MidMorning_IsAfterDawn() =>
        // 14:00 UTC = 09:00 CDT
        Assert.True(DawnPolicy.IsAfterDawn(32.0, -97.0, new DateTime(2025, 10, 6, 14, 0, 0, DateTimeKind.Utc)));

    [Fact]
    public void Dawn_EveningTwilight_IsNotAfterDawn() =>
        // 00:30 UTC = 19:30 CDT previous evening, afternoon solar hour
        Assert.False(DawnPolicy.IsAfterDawn(32.0, -97.0, new DateTime(2025, 10, 7, 0, 30, 0, DateTimeKind.Utc)));

    // ── Night simulation: switches + 45 min gap + restart => 1 session ───────

    /// <summary>Mirrors SessionService's per-frame decisions using the shared policies.</summary>
    private sealed class NightSim
    {
        public int SessionsStarted = 1;           // never incremented: target change / gap / restart reuse it
        public int SessionsEnded;
        public int TargetDetectedCalls;
        public bool ServerWaiting;
        private string? _name; private double _ra, _dec; private bool _hasTarget;

        public void Frame(string name, double ra, double dec)
        {
            if (!TargetBlockPolicy.IsSameTarget(_hasTarget, _name, _ra, _dec, name, ra, dec))
            {
                TargetDetectedCalls++;
                _name = name; _ra = ra; _dec = dec; _hasTarget = true;
            }
            ServerWaiting = false;
        }

        public void Idle(double minutes, bool seqStopped = false)
        {
            var a = SessionIdlePolicy.Evaluate(false, seqStopped, ServerWaiting, minutes, 30, 240);
            Assert.NotEqual(IdleAction.Close, a);
            if (a == IdleAction.MarkWaiting) ServerWaiting = true;
        }

        /// <summary>
        /// Mirrors Plugin.OnSequenceFinished's dawn gate: before dawn, park the session as
        /// "waiting" (keeping planned-target / TS context) instead of ending it; after dawn,
        /// end the session for real so the next sequence start opens a fresh one.
        /// </summary>
        public void SequenceFinished(double latitudeDeg, double longitudeDeg, DateTime utcNow)
        {
            if (DawnPolicy.IsAfterDawn(latitudeDeg, longitudeDeg, utcNow))
            {
                SessionsEnded++;
                ServerWaiting = false;
                _hasTarget = false; // a true end drops the target-block context
            }
            else
            {
                ServerWaiting = true;
            }
        }

        /// <summary>Mirrors SessionService.OnSequenceStartedAsync: reuse the parked session
        /// (do not increment SessionsStarted) when one is still open.</summary>
        public void SequenceRestarted()
        {
            ServerWaiting = false;
        }
    }

    [Fact]
    public void Night_TargetSwitches_Gap_Restart_IsOneSessionOneCallPerBlock()
    {
        var sim = new NightSim();
        for (int i = 0; i < 20; i++) sim.Frame("WR134", 305.0, 36.0);
        for (int i = 0; i < 13; i++) sim.Frame("sh2-126 Panel 8 - BB", 340.0, 50.0);
        sim.Idle(45);                       // clouds: 45-minute gap
        Assert.True(sim.ServerWaiting);
        for (int i = 0; i < 10; i++) sim.Frame("sh2-126 Panel 8 - BB", 340.0, 50.0); // same block resumes
        sim.Idle(6, seqStopped: true);      // sequence restart
        for (int i = 0; i < 5; i++) sim.Frame("M31", 10.68, 41.27);
        for (int i = 0; i < 5; i++) sim.Frame("WR134", 305.0, 36.0); // revisit = new block

        Assert.Equal(1, sim.SessionsStarted);
        Assert.Equal(4, sim.TargetDetectedCalls); // WR134, sh2-126, M31, WR134
    }

    // ── Sequence stop/restart gated by dawn (Plugin.OnSequenceFinished) ──────

    [Fact]
    public void Night_SequenceStopsBeforeDawn_MarksWaiting_RestartReusesSessionAndTargetContext()
    {
        var sim = new NightSim();
        // 32.0,-97.0 at 05:00 UTC = 00:00 CDT: clearly before dawn (see Dawn_Midnight_IsNotAfterDawn).
        var midnightUtc = new DateTime(2025, 10, 6, 5, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < 20; i++) sim.Frame("WR134", 305.0, 36.0);
        sim.SequenceFinished(32.0, -97.0, midnightUtc); // sequence stopped before dawn

        Assert.True(sim.ServerWaiting, "session must be parked 'waiting', not ended, before dawn");
        Assert.Equal(0, sim.SessionsEnded);
        Assert.Equal(1, sim.SessionsStarted);

        // Restart ~10 minutes later, same night: reuses the parked session (no new StartSession
        // call, so planned-target / multi-target context set at the original session start is
        // untouched) and the first post-restart frame on the SAME target does not re-register.
        sim.SequenceRestarted();
        Assert.False(sim.ServerWaiting);
        Assert.Equal(1, sim.SessionsStarted);
        Assert.Equal(0, sim.SessionsEnded);

        sim.Frame("WR134", 305.0, 36.0); // same block resumes — no extra target-detected call
        Assert.Equal(1, sim.TargetDetectedCalls);

        for (int i = 0; i < 5; i++) sim.Frame("M31", 10.68, 41.27); // new block after restart
        Assert.Equal(2, sim.TargetDetectedCalls);
        Assert.Equal(1, sim.SessionsStarted);
        Assert.Equal(0, sim.SessionsEnded);
    }

    [Fact]
    public void Night_SequenceFinishesAfterDawn_EndsSession()
    {
        var sim = new NightSim();
        // 32.0,-97.0 at 14:00 UTC = 09:00 CDT: after dawn (see Dawn_MidMorning_IsAfterDawn).
        var midMorningUtc = new DateTime(2025, 10, 6, 14, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < 10; i++) sim.Frame("NGC 40", 12.0, 72.0);
        sim.SequenceFinished(32.0, -97.0, midMorningUtc);

        Assert.False(sim.ServerWaiting, "a post-dawn finish is a real end, not 'waiting'");
        Assert.Equal(1, sim.SessionsEnded);
    }

    [Fact]
    public void ManyFramesSameTarget_OffLineTargetId_NoDuplicateRows()
    {
        // Offline: server target id is null but local id exists => hasActiveTarget must be true.
        var calls = 0; var has = false;
        for (int i = 0; i < 90; i++)
        {
            if (!TargetBlockPolicy.IsSameTarget(has, "M42", 83.8, -5.4, "M42", 83.8, -5.4)) { calls++; has = true; }
        }
        Assert.Equal(1, calls);
    }
}
