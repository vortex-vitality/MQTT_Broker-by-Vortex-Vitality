# Contributing

Thanks for contributing to **MQTT Broker by Vortex Vitality**! This project exists so the community can improve the Vprobe → Home Assistant bridge for everyone.

## Quick rules

- Be respectful and constructive (see [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)).
- Keep PRs small and focused.
- If you’re planning a larger change, open an Issue first.

## How to contribute

### Report a bug
Open an Issue and include:
- What you expected vs what happened
- Steps to reproduce
- Relevant logs (remove secrets/tokens)
- Your setup: Home Assistant version, MQTT broker, OS

### Request a feature
Open an Issue describing:
- The use case
- Proposed behavior (topics/payloads if relevant)
- Any Home Assistant expectations (Discovery, availability, etc.)

### Submit a pull request
1. Fork the repo
2. Create a branch:
   - `fix/<name>` or `feat/<name>`
3. Make your changes
4. Test locally (at least: login → link device → verify entities + state updates in HA)
5. Open a PR with a clear description and screenshots/logs if helpful

## Coding guidelines

- Prefer clarity over cleverness
- Avoid breaking existing topic/payload behavior unless discussed first
- Don’t add credentials, tokens, or private endpoints to the repo

## Licensing

By submitting a contribution, you agree it can be redistributed under the project’s **MIT License**.
