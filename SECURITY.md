# Security policy

Akinly clusters text that may be private to whoever uses it, and can send that text to a language model. Please report a security problem privately, so that it can be fixed before it is public.

## Reporting a vulnerability

Use GitHub's private vulnerability reporting: on the repository's **Security** tab, choose **Report a vulnerability**. Please do not open a public issue, pull request or discussion for a vulnerability. If the form is not available, open an issue that asks for a private contact and says nothing about the problem itself.

Describe the problem and how to reproduce it with made-up data. You can expect an acknowledgment within a week and an assessment after that. When the fix ships, a GitHub Security Advisory describes it (with a CVE where warranted) and credits you if you want. Please allow up to 90 days before disclosing the problem yourself. Akinly is maintained by one person and offers no service-level agreement; a report with a minimal reproduction is the fastest to fix.

A problem in a model provider's SDK, Microsoft.Extensions.AI or a vector database belongs to that project; report it there, and tell us if Akinly needs to work around it.

## What counts

For example:

- An item's text quoted in an exception message or a log entry.
- Text sent to a model or a store the caller did not configure.
- A crafted input (an embedding, an id, a model's answer) that makes Akinly hang, exhaust memory, or corrupt the clusters it returns.

**Not vulnerabilities:** two different items placed in one cluster, or one thing split across two. That is clustering quality; report it as an ordinary issue with a made-up example.

## Supported versions

Fixes go into the latest release only: upgrade to it to receive them.
