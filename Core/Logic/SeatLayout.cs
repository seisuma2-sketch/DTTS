using System;
using System.Collections.Generic;

namespace DTTS.Core.Logic;

/// <summary>
/// 対戦画面の席の配置。自分を画面の下に置き、エンジンの座席順（<see cref="GameEngine.SeatOrder"/>）で
/// 自分の次の人から順に 左 → 上 → 右 と並べる。
/// <para>
/// エンジンの時計回り（<see cref="Enums.PlayDirection.Clockwise"/>）は座席順に進むので、この配置にすると
/// 画面上でも 下 → 左 → 上 → 右 → 下 と時計回りに手番が回る（リバース中は逆回り）。
/// 席替えで座席順が変わっても、この関数で配置し直せば画面と進行の向きは常に一致する。
/// </para>
/// </summary>
public static class SeatLayout
{
    /// <summary>画面の下（自分）。</summary>
    public const int Bottom = 0;

    /// <summary>画面の左（自分の次に手番が来る人）。</summary>
    public const int Left = 1;

    /// <summary>画面の上（向かい）。</summary>
    public const int Top = 2;

    /// <summary>画面の右（自分の前に手番が来る人）。</summary>
    public const int Right = 3;

    /// <summary>
    /// プレイヤーを表示する席（自分から座席順に数えた位置。4人なら 0=下・1=左・2=上・3=右）。
    /// </summary>
    /// <param name="seatOrder">座席順（プレイヤーIDの並び）。</param>
    /// <param name="viewerId">画面の持ち主（下に表示する自分）のプレイヤーID。</param>
    /// <param name="playerId">席を求めるプレイヤーID。</param>
    /// <returns>席の位置。座席順にいないIDなら -1。</returns>
    public static int SlotOf(IReadOnlyList<int> seatOrder, int viewerId, int playerId)
    {
        ArgumentNullException.ThrowIfNull(seatOrder);
        int n = seatOrder.Count;
        int viewer = IndexOf(seatOrder, viewerId);
        int player = IndexOf(seatOrder, playerId);
        if (n == 0 || viewer < 0 || player < 0)
        {
            return -1;
        }
        return ((player - viewer) % n + n) % n;
    }

    /// <summary>指定の席に座っているプレイヤーID（<see cref="SlotOf"/> の逆）。</summary>
    public static int PlayerAt(IReadOnlyList<int> seatOrder, int viewerId, int slot)
    {
        ArgumentNullException.ThrowIfNull(seatOrder);
        int n = seatOrder.Count;
        int viewer = IndexOf(seatOrder, viewerId);
        if (n == 0 || viewer < 0)
        {
            return -1;
        }
        return seatOrder[((viewer + slot) % n + n) % n];
    }

    private static int IndexOf(IReadOnlyList<int> list, int value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
            {
                return i;
            }
        }
        return -1;
    }
}
