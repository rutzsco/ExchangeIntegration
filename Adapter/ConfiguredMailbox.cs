using ExchangeIntegration.Configuration;
using ExchangeIntegration.Exchange;

namespace ExchangeIntegration.Adapter;

/// <summary>One mailbox's settings and EWS client, shared by its poller and the command handler.</summary>
public sealed record ConfiguredMailbox(ExchangeOptions Options, IExchangeMailbox Client);
