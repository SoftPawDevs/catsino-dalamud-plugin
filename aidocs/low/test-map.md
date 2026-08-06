# Test Map

## Protocol And Serialization

- `tests/Catsino.Plugin.Tests/ApiProtocolTests.cs`: backend API contract expectations.
- `tests/Catsino.Plugin.Tests/ProtocolFixtureTests.cs`: fixture compatibility and protocol shape.
- `tests/Catsino.Plugin.Tests/ContractSerializationTests.cs`: DTO serialization behavior.

## Runtime And State

- `tests/Catsino.Plugin.Tests/DealerSessionStateTests.cs`: session and roster state behavior.

## Security And Validation

- `tests/Catsino.Plugin.Tests/ValidationAndSecurityTests.cs`: validation and secret-sensitive behavior.

## Durable Outbox And Payout

- `tests/Catsino.Plugin.Tests/PersistentPayoutOutboxTests.cs`: outbox persistence and replay safety.
- `tests/Catsino.Plugin.Tests/PayoutExecutionPolicyTests.cs`: payout gating rules.
- `tests/Catsino.Plugin.Tests/PayoutCoordinatorTests.cs`: payout orchestration.

## Dropbox Integration

- `tests/Catsino.Dropbox.IntegrationTests/TradeCompletionDetectorTests.cs`: language-independent trade completion behavior.

## How To Use This Map

When you change a contract, payout rule, runtime state transition, or security check, update the nearest test coverage in the same area.
