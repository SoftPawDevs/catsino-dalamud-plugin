# Test Map

## Protocol And Serialization

- `tests/Catsino.Plugin.Tests/ApiProtocolTests.cs`: backend API contract expectations.
- `tests/Catsino.Plugin.Tests/ProtocolFixtureTests.cs`: fixture compatibility and protocol shape.
- `tests/Catsino.Plugin.Tests/ContractSerializationTests.cs`: DTO serialization behavior and the version pin — `VersionIsStable` asserts `ContractVersion.Current == "1.5.0"` and `PluginVersion.Current == "1.5.5"` (bump both here when releasing).

## Runtime And State

- `tests/Catsino.Plugin.Tests/DealerSessionStateTests.cs`: session and roster state behavior.

## Security And Validation

- `tests/Catsino.Plugin.Tests/ValidationAndSecurityTests.cs`: validation and secret-sensitive behavior.

## Durable Outbox And Payout

- `tests/Catsino.Plugin.Tests/PayoutBatchCoordinatorTests.cs`: client-driven cash-out batch — full-success
  single settle, partial-fail settle, ambiguous, the durable `Trading` confirm-barrier signal, restart
  quarantine of a mid-trade leg (never re-run), resume of a pending leg, and settlement retry.
- `tests/Catsino.Plugin.Tests/PersistentPayoutOutboxTests.cs`: outbox persistence and replay safety (legacy).
- `tests/Catsino.Plugin.Tests/PayoutExecutionPolicyTests.cs`: payout gating rules.
- `tests/Catsino.Plugin.Tests/PayoutCoordinatorTests.cs`: legacy per-leg coordinator orchestration.

## Trade Execution

- `tests/Catsino.Plugin.Tests/PayoutCoordinatorTests.cs`: payout executor orchestration and exact event matching.

## Coverage Gaps To Know

- The Blackjack dealer surface (`Ui/BlackjackPanelRenderer.cs`, `Ui/CardTextures.cs`, `Runtime/BlackjackTableStore.cs`, the `RefreshBlackjackTablesAsync` poll, and the `GetBlackjackTableAsync`/`Deal`/`Hit`/`Stay` API calls) has **no dedicated automated tests** — the `BlackjackTableDto` and its action requests are exercised only via the shared JSON options; verify the table/controls by running the plugin against a live backend. The authoritative Blackjack rules and turn engine are tested in the **web** repo (`BlackjackTests` + `BlackjackServiceTests`).

## How To Use This Map

When you change a contract, payout rule, runtime state transition, or security check, update the nearest test coverage in the same area.
