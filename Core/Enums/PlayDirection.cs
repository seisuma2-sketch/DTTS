namespace DTTS.Core.Enums;

/// <summary>手番の進行方向。</summary>
public enum PlayDirection : byte
{
    /// <summary>時計回り（座席番号が増える方向）</summary>
    Clockwise = 0,
    /// <summary>反時計回り</summary>
    CounterClockwise = 1,
}
