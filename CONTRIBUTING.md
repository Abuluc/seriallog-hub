# Contributing to SerialLog Hub

Thank you for your interest in **SerialLog Hub**!

## Development Workflow

1. Fork the repository and create a feature branch (`git checkout -b feat/your-feature`).
2. Follow Microsoft C# naming conventions and keep all UI text, API messages, and code comments in English.
3. Keep physical hardware transport (`RealSerialTransport`) isolated from simulation/demo logic (`SimulatedSerialTransport` / `CloudDemoResponderService`).
4. Run the full build and test suite before opening a Pull Request:
   ```powershell
   dotnet build SerialLogHub.sln
   dotnet test SerialLogHub.sln
   ```
5. Never commit real credentials, IP addresses, `.db` files, `.env` files, or build artifacts.
