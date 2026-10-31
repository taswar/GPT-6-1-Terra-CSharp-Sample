# Contributing

Thanks for helping improve this sample. It's a small .NET 10 console app with two examples that call GPT-6.1 Terra on Microsoft Foundry through the Responses API: one reads a screenshot, the other reviews a PDF invoice with a tool call and structured output. Contributions that make it clearer, more correct, or more useful to other .NET developers are welcome.

## Ways to contribute

- **Report a bug.** An example fails, or the README doesn't match what the code does.
- **Fix the docs.** Typos, unclear steps, or a missing troubleshooting case.
- **Improve the sample.** For example, a streaming version of Example 1, or a clearer way to show the tool loop in Example 2.
- **Share results.** If you compared `detail: high` and `low`, or different reasoning efforts, on your own images or invoices, an issue with the token counts helps others.

For anything larger than a small fix, open an issue first so we can agree on the approach before you write code.

## Set up

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and the Azure CLI.
2. Clone the repo and build it:

   ```bash
   git clone https://github.com/taswar/GPT-6-1-Terra-CSharp-Sample.git
   cd GPT-6-1-Terra-CSharp-Sample
   dotnet build
   ```

3. To run it, deploy GPT-6.1 Terra on a Microsoft Foundry resource, set `AZURE_OPENAI_ENDPOINT` and `AZURE_OPENAI_DEPLOYMENT` with user secrets, and sign in with `az login`, as described in the [README](README.md#run-it). For Entra ID, your account needs the **Cognitive Services OpenAI User** role on the resource. An API key in `AZURE_OPENAI_API_KEY` works too.

## Guidelines

- **Never commit secrets.** No keys, endpoints, tenant IDs or tokens in code, config files or screenshots. Settings belong in user secrets or environment variables.
- **Keep Entra ID as the default.** The API key is optional. Don't make it required.
- **Keep the raw Responses API calls.** The sample posts JSON with `HttpClient` on purpose, so readers can see the request shape. Don't swap it for an SDK wrapper or switch to `/chat/completions`.
- **Keep the tool read-only and simulated.** `check_supplier` only looks up a hard-coded dictionary. Don't add tools that write, pay or send, and don't connect the sample to a real system.
- **Keep the schema and record in sync.** If you change the `invoice_review` schema, update the `InvoiceReview` record in the same change. Strict mode needs every property in `required` and `additionalProperties` set to `false`.
- **Keep the sample files small and fake.** `expense-form.png` and `invoice.pdf` must not contain real people, companies or financial data.
- **Keep the usage line.** Every example prints its token usage and time. Keep the `Usage` report for any new example, so readers can see the cost.
- **Keep the timeouts.** One five-minute budget covers the run, and each request has a two-minute network timeout. Keep them, or explain why you changed them.
- **Keep it a sample.** The code should stay short and readable in one `Program.cs`. Prefer clarity over abstraction, and add a dependency only when it clearly helps.
- **Match the existing style.** Target `net10.0`, keep nullable reference types on, and follow the layout already in `Program.cs`.
- **Keep the README in sync.** If you add an example, change settings or change output, update the README in the same pull request, including the "What's inside" table and the usage text in `Program.cs`.
- **One change per pull request.** Small, focused pull requests are easier to review and merge.

## Pull request checklist

- [ ] `dotnet build` succeeds with no new warnings.
- [ ] Any example you changed runs against your own GPT-6.1 Terra deployment, or you explain why you couldn't run it.
- [ ] No secrets, endpoints or personal data are in the diff.
- [ ] The README reflects any change in examples, settings or output.
- [ ] The pull request description says what changed and why.

## Reporting a bug

Open an issue and include:

- Which example failed (`1` or `2`) and what you expected.
- The full error message, with any endpoints removed.
- Your .NET SDK version (`dotnet --version`) and operating system.
- Whether you used Entra ID or an API key, and whether the error was a timeout.

## Security issues

Please don't report security problems in a public issue. Use **Security → Report a vulnerability** on the repo to send a private report.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE) that covers this project.
