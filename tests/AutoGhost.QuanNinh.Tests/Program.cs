using AutoGhost.TaskEngine;

internal static class Program
{
    private static readonly DateTimeOffset MondayLunchUtc =
        new(2026, 9, 7, 5, 0, 0, TimeSpan.Zero);

    private static int Main()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("default_policy_and_timezone", TestDefaultPolicyAndTimezone),
            ("window_boundaries_are_end_exclusive", TestWindowBoundariesAreEndExclusive),
            ("allowed_days_are_monday_wednesday_friday_sunday", TestAllowedDays),
            ("quota_counts_verified_success_only", TestQuotaCountsVerifiedSuccessOnly),
            ("quota_persists_across_restart_and_isolates_clients", TestQuotaPersistenceAndIsolation),
            ("local_date_key_uses_vietnam_timezone", TestLocalDateKeyUsesVietnamTimezone),
            ("live_gates_fail_closed_until_verified", TestLiveGatesFailClosedUntilVerified),
            ("input_guard_requires_identity_foreground_and_gates", TestInputGuard)
        };

        try
        {
            foreach (var test in tests)
            {
                test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }

            Console.WriteLine($"P4 Quan Ninh scheduler tests: PASS ({tests.Length}/{tests.Length})");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"P4 Quan Ninh scheduler tests: FAIL {exception}");
            return 1;
        }
    }

    private static void TestDefaultPolicyAndTimezone()
    {
        var path = CreateTempPath();
        try
        {
            var service = new QuanNinhSchedulerService(
                new JsonQuanNinhRegistrationHistoryStore(path));

            var decision = service.Evaluate("client-a", MondayLunchUtc);

            Assert(decision.IsEligible, "Monday at 12:00 Vietnam time was not eligible.");
            Assert(decision.LocalDate == new DateOnly(2026, 9, 7),
                "The scheduler did not resolve the configured Vietnam local date.");
            Assert(decision.LocalTime == new TimeOnly(12, 0),
                "The scheduler did not resolve the configured Vietnam local time.");
            Assert(decision.RemainingSuccessfulRegistrations == 3,
                "The default daily success quota was not three.");
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    private static void TestWindowBoundariesAreEndExclusive()
    {
        var path = CreateTempPath();
        try
        {
            var service = new QuanNinhSchedulerService(
                new JsonQuanNinhRegistrationHistoryStore(path));

            var lunchStart = service.Evaluate(
                "client-a",
                new DateTimeOffset(2026, 9, 7, 5, 0, 0, TimeSpan.Zero));
            var lunchEnd = service.Evaluate(
                "client-a",
                new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero));
            var eveningStart = service.Evaluate(
                "client-a",
                new DateTimeOffset(2026, 9, 7, 13, 0, 0, TimeSpan.Zero));
            var eveningEnd = service.Evaluate(
                "client-a",
                new DateTimeOffset(2026, 9, 7, 14, 0, 0, TimeSpan.Zero));

            Assert(lunchStart.IsEligible, "12:00 was not included in the first window.");
            Assert(lunchEnd.Status == QuanNinhEligibilityStatus.OutsideRegistrationWindow,
                "13:00 was incorrectly included in the first window.");
            Assert(eveningStart.IsEligible, "20:00 was not included in the second window.");
            Assert(eveningEnd.Status == QuanNinhEligibilityStatus.OutsideRegistrationWindow,
                "21:00 was incorrectly included in the second window.");
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    private static void TestAllowedDays()
    {
        var path = CreateTempPath();
        try
        {
            var service = new QuanNinhSchedulerService(
                new JsonQuanNinhRegistrationHistoryStore(path));
            var utcAtLunch = new[]
            {
                new DateTimeOffset(2026, 9, 7, 5, 0, 0, TimeSpan.Zero), // Monday
                new DateTimeOffset(2026, 9, 9, 5, 0, 0, TimeSpan.Zero), // Wednesday
                new DateTimeOffset(2026, 9, 11, 5, 0, 0, TimeSpan.Zero), // Friday
                new DateTimeOffset(2026, 9, 13, 5, 0, 0, TimeSpan.Zero) // Sunday
            };

            foreach (var timestamp in utcAtLunch)
            {
                Assert(service.Evaluate("client-a", timestamp).IsEligible,
                    $"Allowed day was rejected: {timestamp:yyyy-MM-dd}.");
            }

            var Tuesday = service.Evaluate(
                "client-a",
                new DateTimeOffset(2026, 9, 8, 5, 0, 0, TimeSpan.Zero));
            Assert(Tuesday.Status == QuanNinhEligibilityStatus.NotAllowedDay,
                "Tuesday was incorrectly accepted.");
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    private static void TestQuotaCountsVerifiedSuccessOnly()
    {
        var path = CreateTempPath();
        try
        {
            var service = new QuanNinhSchedulerService(
                new JsonQuanNinhRegistrationHistoryStore(path));

            var before = service.Evaluate("client-a", MondayLunchUtc);
            Assert(before.SuccessfulRegistrationsToday == 0,
                "A schedule evaluation incorrectly counted an attempt as success.");

            service.RecordVerifiedSuccess("client-a", MondayLunchUtc);
            var afterOne = service.Evaluate("client-a", MondayLunchUtc);
            Assert(afterOne.SuccessfulRegistrationsToday == 1 && afterOne.RemainingSuccessfulRegistrations == 2,
                "A verified success did not consume exactly one quota slot.");

            service.RecordVerifiedSuccess("client-a", MondayLunchUtc);
            service.RecordVerifiedSuccess("client-a", MondayLunchUtc);
            var afterThree = service.Evaluate("client-a", MondayLunchUtc);
            Assert(afterThree.Status == QuanNinhEligibilityStatus.DailySuccessLimitReached,
                "The third verified success did not close the daily quota.");
            Assert(afterThree.RemainingSuccessfulRegistrations == 0,
                "Remaining quota was not zero after three verified successes.");

            AssertThrows<InvalidOperationException>(
                () => service.RecordVerifiedSuccess("client-a", MondayLunchUtc),
                "A fourth verified success was accepted.");
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    private static void TestQuotaPersistenceAndIsolation()
    {
        var path = CreateTempPath();
        try
        {
            var store = new JsonQuanNinhRegistrationHistoryStore(path);
            var service = new QuanNinhSchedulerService(store);
            service.RecordVerifiedSuccess("client-a", MondayLunchUtc);
            service.RecordVerifiedSuccess("client-a", MondayLunchUtc);

            var reloaded = new QuanNinhSchedulerService(
                new JsonQuanNinhRegistrationHistoryStore(path));
            var clientA = reloaded.Evaluate("client-a", MondayLunchUtc);
            var clientB = reloaded.Evaluate("client-b", MondayLunchUtc);
            var Wednesday = reloaded.Evaluate(
                "client-a",
                new DateTimeOffset(2026, 9, 9, 5, 0, 0, TimeSpan.Zero));

            Assert(clientA.SuccessfulRegistrationsToday == 2,
                "Successful registration count did not survive restart.");
            Assert(clientA.RemainingSuccessfulRegistrations == 1,
                "Reloaded client quota was incorrect.");
            Assert(clientB.SuccessfulRegistrationsToday == 0 && clientB.IsEligible,
                "Client quota/history leaked across Client IDs.");
            Assert(Wednesday.SuccessfulRegistrationsToday == 0 && Wednesday.IsEligible,
                "A new local date did not receive a fresh daily quota.");
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    private static void TestLocalDateKeyUsesVietnamTimezone()
    {
        var path = CreateTempPath();
        try
        {
            var service = new QuanNinhSchedulerService(
                new JsonQuanNinhRegistrationHistoryStore(path));
            var justAfterMidnightVietnam = new DateTimeOffset(
                2026, 9, 6, 17, 0, 0, TimeSpan.Zero);

            service.RecordVerifiedSuccess("client-a", justAfterMidnightVietnam);

            var store = new JsonQuanNinhRegistrationHistoryStore(path);
            Assert(store.Load("client-a", new DateOnly(2026, 9, 7))?.SuccessfulRegistrations == 1,
                "The registration was not keyed by Vietnam local date.");
            Assert(store.Load("client-a", new DateOnly(2026, 9, 6)) is null,
                "The registration was incorrectly keyed by UTC date.");
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    private static void TestLiveGatesFailClosedUntilVerified()
    {
        var pending = new QuanNinhLiveGateEvidence(
            QuanNinhLiveGateGuard.SlotRecognitionGate,
            QuanNinhLiveGateState.PendingLiveObservation,
            null,
            "qnyh live observation is pending");
        var verified = new QuanNinhLiveGateEvidence(
            QuanNinhLiveGateGuard.RegistrationConfirmationGate,
            QuanNinhLiveGateState.Verified,
            "live-evidence-placeholder",
            "qnyh success state was visibly confirmed");

        AssertThrows<InvalidOperationException>(
            () => QuanNinhLiveGateGuard.RequireVerified(pending, verified),
            "A pending Q-QN gate did not fail closed.");

        var verifiedSlot = pending with
        {
            State = QuanNinhLiveGateState.Verified,
            EvidencePath = "live-evidence-placeholder",
            Reason = "qnyh slot was visibly confirmed"
        };
        QuanNinhLiveGateGuard.RequireVerified(verifiedSlot, verified);
    }

    private static void TestInputGuard()
    {
        var verifiedSlot = new QuanNinhLiveGateEvidence(
            QuanNinhLiveGateGuard.SlotRecognitionGate,
            QuanNinhLiveGateState.Verified,
            "live-evidence-placeholder",
            "qnyh slot was visibly confirmed");
        var verifiedRegistration = new QuanNinhLiveGateEvidence(
            QuanNinhLiveGateGuard.RegistrationConfirmationGate,
            QuanNinhLiveGateState.Verified,
            "live-evidence-placeholder",
            "qnyh success state was visibly confirmed");
        var target = new QuanNinhLiveTarget(
            ClientId: "client-a",
            RoleId: "client-a",
            WindowHandle: new nint(0x1234),
            IsForeground: true,
            KillSwitchActive: false,
            AutomationArmed: true);

        QuanNinhLiveInteractionGuard.RequireReady(
            target,
            verifiedSlot,
            verifiedRegistration);

        AssertThrows<InvalidOperationException>(
            () => QuanNinhLiveInteractionGuard.RequireReady(
                target with { RoleId = "different-role" },
                verifiedSlot,
                verifiedRegistration),
            "Mismatched Role ID was allowed through the input guard.");
        AssertThrows<InvalidOperationException>(
            () => QuanNinhLiveInteractionGuard.RequireReady(
                target with { IsForeground = false },
                verifiedSlot,
                verifiedRegistration),
            "A non-foreground target was allowed through the input guard.");
        AssertThrows<InvalidOperationException>(
            () => QuanNinhLiveInteractionGuard.RequireReady(
                target with { KillSwitchActive = true },
                verifiedSlot,
                verifiedRegistration),
            "Kill Switch did not block Quan Ninh input.");
    }

    private static string CreateTempPath()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "AutoGhost-P4-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "quan-ninh-history.json");
    }

    private static void DeleteTempPath(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
