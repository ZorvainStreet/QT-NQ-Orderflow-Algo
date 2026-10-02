namespace QT_MNQ_Orderflow_Algo.Data;

public enum Aggressor { Unknown, Buy, Sell }

/// <summary>A trade print as it arrives from the feed, before any filtering.</summary>
public readonly record struct RawTrade(DateTime Utc, decimal Price, decimal Size, Aggressor Side);

public readonly record struct RawQuote(DateTime Utc, decimal Bid, decimal Ask);

/// <summary>A trade that passed the sanity filter: tick-rounded price and a definite side.</summary>
public readonly record struct Trade(DateTime Utc, decimal Price, decimal Size, bool IsBuy, bool UsedFallback);
