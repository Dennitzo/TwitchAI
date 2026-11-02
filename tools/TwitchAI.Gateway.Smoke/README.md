# Gateway live acceptance

Run against an already running TwitchAI-owned gateway and native runtime:

```powershell
dotnet run --project tools/TwitchAI.Gateway.Smoke -- --model 'coding/...' --reasoning low --max-tokens 2048 --output artifacts/validation/gateway-live-smoke.json
python tools/TwitchAI.Gateway.Smoke/verify-stream.py --model 'coding/...'
```

The tool uses the actual application client, staged compiler and validators. It
checks gateway health/catalog, explicit chunked model load, native loaded state,
idempotent warmup, structured errors, real classification/confirmation of five
fixed local samples through the application client, plus explicit chunked and
fixed Content-Length classification requests. Exit code zero
requires every protocol, validation and semantic expectation to pass. The JSON
report includes model responses and usage metrics. No Twitch calls or application
database writes are performed. `--case NAME` narrows the batch for diagnosis;
`--max-tokens NUMBER` changes the output budget (default 8192).
The documented 2048-token setting matches the application's default. The stream
probe additionally verifies a real SSE response and the model's `none` reasoning
setting with a fixed greeting and JSON schema.
