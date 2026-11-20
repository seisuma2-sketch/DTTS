import { motion } from "framer-motion";
import { Crown, RotateCcw, Sparkles } from "lucide-react";

type Suit = "♠" | "♥" | "♦" | "♣";
const isRed = (s: Suit) => s === "♥" || s === "♦";

function PlayingCard({
  rank,
  suit,
  className = "",
  glow = false,
  size = "md",
}: {
  rank: string;
  suit: Suit;
  className?: string;
  glow?: boolean;
  size?: "sm" | "md";
}) {
  const dims = size === "sm" ? "h-[60px] w-[42px] sm:h-[78px] sm:w-[54px]" : "h-[72px] w-[50px] sm:h-[104px] sm:w-[72px]";
  return (
    <div
      className={`relative flex ${dims} flex-col justify-between rounded-[8px] border border-white/60 bg-gradient-to-br from-white to-[#dfe9f7] p-1.5 shadow-[0_10px_30px_-10px_rgba(0,0,0,0.8)] sm:rounded-[10px] sm:p-2 ${
        glow ? "ring-2 ring-azure-cyan/80 shadow-[0_0_28px_rgba(34,211,238,0.55)]" : ""
      } ${className}`}
    >
      <span className={`text-[11px] font-bold leading-none sm:text-sm ${isRed(suit) ? "text-rose-500" : "text-slate-900"}`}>
        {rank}
        <br />
        {suit}
      </span>
      <span className={`self-center text-lg sm:text-3xl ${isRed(suit) ? "text-rose-500" : "text-slate-900"}`}>{suit}</span>
    </div>
  );
}

function CardBack({ className = "" }: { className?: string }) {
  return (
    <div
      className={`h-[34px] w-[24px] rounded-[5px] border border-azure-soft/40 bg-[repeating-linear-gradient(45deg,#0B1F3F_0_4px,#123063_4px_8px)] shadow-md sm:h-[46px] sm:w-[32px] ${className}`}
    />
  );
}

function Seat({
  name,
  count,
  rank,
  turn = false,
  className = "",
}: {
  name: string;
  count: number;
  rank: string;
  turn?: boolean;
  className?: string;
}) {
  return (
    <div className={`flex flex-col items-center gap-2 ${className}`}>
      <div className="flex -space-x-3 sm:-space-x-4">
        {Array.from({ length: Math.min(count, 6) }).map((_, i) => (
          <CardBack key={i} className="" />
        ))}
      </div>
      <div
        className={`flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-[10px] backdrop-blur-md sm:text-xs ${
          turn ? "border-azure-cyan/60 bg-azure/20 text-ice shadow-[0_0_20px_rgba(34,211,238,0.35)]" : "border-white/10 bg-white/5 text-ice-body"
        }`}
      >
        <span className="font-semibold">{name}</span>
        <span className="text-ice-mute">· {count}枚</span>
        <span className="hidden text-azure-soft sm:inline">· {rank}</span>
      </div>
    </div>
  );
}

const hand: { rank: string; suit: Suit; pick?: boolean }[] = [
  { rank: "3", suit: "♣" },
  { rank: "5", suit: "♦" },
  { rank: "6", suit: "♠", pick: true },
  { rank: "6", suit: "♥", pick: true },
  { rank: "9", suit: "♣" },
  { rank: "J", suit: "♦" },
  { rank: "K", suit: "♠" },
  { rank: "2", suit: "♥" },
];

/** ヒーロー用の対戦卓モックアップ（革命中の 4 人卓） */
export default function TableMockup() {
  return (
    <div className="relative">
      {/* 卓下の光 */}
      <div className="absolute -inset-x-10 -bottom-10 top-1/3 rounded-full bg-azure/20 blur-[90px]" />
      <div className="glass relative aspect-[4/5] overflow-hidden min-[480px]:aspect-[4/3] rounded-[28px] !bg-[#06142c]/70 shadow-[0_40px_120px_-30px_rgba(0,0,0,0.9)] sm:aspect-[16/10] sm:rounded-[36px]">
        {/* フェルト */}
        <div className="absolute inset-3 rounded-[22px] bg-[radial-gradient(ellipse_at_center,#0f2b57_0%,#0a1c3c_55%,#061329_100%)] sm:inset-5 sm:rounded-[28px]" />
        <div className="absolute inset-3 rounded-[22px] border border-white/[0.06] sm:inset-5 sm:rounded-[28px]" />
        {/* 革命中のオーラ */}
        <motion.div
          className="absolute left-1/2 top-1/2 h-[70%] w-[70%] -translate-x-1/2 -translate-y-1/2 rounded-full bg-[conic-gradient(from_0deg,rgba(34,211,238,0.0),rgba(34,211,238,0.22),rgba(62,142,247,0.0),rgba(96,165,250,0.22),rgba(34,211,238,0.0))] blur-2xl"
          animate={{ rotate: 360 }}
          transition={{ duration: 18, repeat: Infinity, ease: "linear" }}
        />

        {/* ステータス */}
        <div className="absolute left-5 top-5 flex items-center gap-2 sm:left-9 sm:top-9">
          <span className="flex items-center gap-1 rounded-full border border-azure-cyan/50 bg-azure-cyan/10 px-2.5 py-1 text-[10px] font-semibold text-azure-cyan shadow-[0_0_20px_rgba(34,211,238,0.3)] sm:text-xs">
            <RotateCcw className="h-3 w-3" />
            革命中
          </span>
          <span className="hidden rounded-full border border-white/10 bg-white/5 px-2.5 py-1 text-[10px] text-ice-body sm:inline sm:text-xs">
            ♠♥ 縛り
          </span>
        </div>
        <div className="absolute right-5 top-5 hidden items-center gap-1 text-[11px] text-ice-mute sm:right-9 sm:top-9 sm:flex">
          <Crown className="h-3.5 w-3.5 text-amber-300" />
          第 3 局
        </div>

        {/* 対戦相手 */}
        <Seat name="NPC 北" count={7} rank="大富豪" className="absolute left-1/2 top-[9%] -translate-x-1/2" turn />
        <Seat name="NPC 西" count={5} rank="平民" className="absolute left-[4%] top-[38%] sm:left-[7%]" />
        <Seat name="NPC 東" count={9} rank="貧民" className="absolute right-[4%] top-[38%] sm:right-[7%]" />

        {/* 場札 */}
        <div className="absolute left-1/2 top-[45%] flex -translate-x-1/2 -translate-y-1/2 items-center">
          <motion.div
            initial={{ y: -80, rotate: -30, opacity: 0 }}
            animate={{ y: 0, rotate: -8, opacity: 1 }}
            transition={{ type: "spring", stiffness: 120, damping: 14, delay: 1.2 }}
          >
            <PlayingCard rank="7" suit="♠" glow />
          </motion.div>
          <motion.div
            className="-ml-6 sm:-ml-8"
            initial={{ y: -80, rotate: 30, opacity: 0 }}
            animate={{ y: 6, rotate: 7, opacity: 1 }}
            transition={{ type: "spring", stiffness: 120, damping: 14, delay: 1.35 }}
          >
            <PlayingCard rank="7" suit="♥" glow />
          </motion.div>
        </div>
        <motion.div
          className="absolute left-1/2 top-[59%] flex -translate-x-1/2 items-center gap-1 whitespace-nowrap text-[10px] text-ice-mute min-[480px]:top-[61%] sm:top-[62%] sm:text-xs"
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
          transition={{ delay: 1.8 }}
        >
          <Sparkles className="h-3 w-3 text-azure-cyan" />
          NPC 北 が 7 のペア — 7渡し
        </motion.div>

        {/* 自分の手札 */}
        <div className="absolute bottom-[5%] left-1/2 flex -translate-x-1/2 items-end">
          {hand.map((c, i) => {
            const mid = (hand.length - 1) / 2;
            const off = i - mid;
            return (
              <motion.div
                key={`${c.rank}${c.suit}`}
                className="-ml-5 first:ml-0 sm:-ml-6"
                style={{ rotate: off * 4, transformOrigin: "50% 120%" }}
                initial={{ y: 0 }}
                animate={{ y: c.pick ? -16 : Math.abs(off) * 2 }}
                whileHover={{ y: -22 }}
                transition={{ type: "spring", stiffness: 300, damping: 20 }}
              >
                <PlayingCard rank={c.rank} suit={c.suit} size="sm" glow={c.pick} />
              </motion.div>
            );
          })}
        </div>
      </div>
    </div>
  );
}
