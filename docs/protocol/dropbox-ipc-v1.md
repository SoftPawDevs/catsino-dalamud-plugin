# Dropbox Payout IPC v1

The IPC prefix is `Catsino.Dropbox.Payout.v1`. Payload records are serialized with the matching `Catsino.Dropbox.Contracts` public contract. The surface intentionally accepts only outgoing gil and exposes no inbound trade or deposit automation.

## Functions

- `GetVersion() -> JSON DropboxVersionInfo`
- `GetCapabilities() -> JSON string[]`
- `SupportsLanguageIndependentTradeState() -> bool`
- `EnablePayoutMode(sessionId) -> bool`
- `DisablePayoutMode(sessionId) -> bool`
- `QueueOutgoingGilTrade(operationId, playerName, homeWorld, amount) -> bool`
- `CancelOutgoingTrade(operationId) -> bool`
- `GetTradeOperation(operationId) -> JSON DropboxTradeOperation or empty string`

The queue requires a non-empty operation ID, exact character name, exact Home World, amount from 1 through 1,000,000 gil, enabled payout mode, an unused operation ID, and no active operation. Waiting for the exact nearby loaded targetable player has no timeout. There is no automatic retry.

## Events

- `PlayerDetected`
- `TradeOpened`
- `TradeLocked`
- `TradeCompleted`
- `TradeCancelled`
- `TradeFailed`
- `TradeTimedOut`

Each event contains operation and session identity, exact name and Home World, gil, UTC time, sequence number, Dropbox plugin instance, and optional safe error fields.

The provider instance returned by `GetVersion` is also reported as backend `DropboxCapabilitiesDto.pluginInstanceId` and must match every payout event from that provider instance.

Completion requires accumulated structured proof: the exact loaded player and Home World match the current trade partner state, the sixth local trade slot contains the exact backend amount after its callback, both bounded addon lock states were observed, confirmation was accepted, and the currency inventory shows the exact debit after the trade condition closes. English, German, French, Japanese, and unknown clients use this same state. A generic window close, changed/unavailable addon structure, missing proof, or observer failure after opening produces an ambiguous failed event requiring reconciliation. Localized chat is never a completion signal.
