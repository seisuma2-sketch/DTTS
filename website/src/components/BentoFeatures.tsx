import type { ReactNode } from "react";
import { motion } from "framer-motion";
import { BarChart3, Bluetooth, BrainCircuit, Layers, Lock, Sparkles, Wifi } from "lucide-react";
import Reveal from "./Reveal";

function BentoCard({
  className = "",
  icon,
  eyebrow,
  title,
  children,
  visual,
  delay = 0,
}: {
  className?: string;
  icon: ReactNode;
  eyebrow: string;
  title: ReactNode;
  children: ReactNode;
  visual?: ReactNode;
  delay?: number;
}) {
  return (
    <Reveal delay={delay} className={className}>
      <article className="group relative flex h-full flex-col overflow-hidden rounded-[28px] border border-white/10 bg-white/[0.03] p-7 backdrop-blur-xl transition duration-500 hover:border-azure/50 hover:shadow-[0_0_0_1px_rgba(62,142,247,0.25),0_20px_80px_-20px_rgba(62,142,247,0.45)] sm:p-8">
        <div className="pointer-events-none absolute -right-20 -top-20 h-56 w-56 rounded-full bg-azure/0 blur-3xl transition duration-700 group-hover:bg-azure/20" />
        <div className="relative flex items-center gap-2.5">
          <span className="flex h-9 w-9 items-center justify-center rounded-xl border border-white/10 bg-white/5 text-azure-soft">
            {icon}
          </span>
          <span className="eyebrow">{eyebrow}</span>
        </div>
        <h3 className="relative mt-5 text-balance text-2xl font-bold leading-tight tracking-tight text-ice sm:text-[28px]">{title}</h3>
        <div className="relative mt-3 text-[15px] leading-relaxed text-ice-mute">{children}</div>
        {visual && <div className="relative mt-auto pt-8">{visual}</div>}
      </article>
    </Reveal>
  );
}

const ruleChips = ["革命", "階段革命", "8切り", "4止め", "砂嵐", "スペ3返し", "11バック", "7渡し", "10捨て", "12ボンバー", "都落ち", "下剋上", "天変地異", "激しば"];

function RuleCloud() {
  return (
    <div className="flex flex-wrap gap-2 [mask-image:linear-gradient(to_bottom,black_60%,transparent)]">
      {ruleChips.map((r, i) => (
        <motion.span
          key={r}
          initial={{ opacity: 0, scale: 0.9 }}
          whileInView={{ opacity: 1, scale: 1 }}
          viewport={{ once: true }}
          transition={{ delay: 0.2 + i * 0.04 }}
          className="rounded-full border border-white/10 bg-white/[0.04] px-3 py-1.5 text-xs text-ice-body transition group-hover:border-azure/30"
        >
          {r}
        </motion.span>
      ))}
    </div>
  );
}

function SignalVisual() {
  return (
    <div className="relative flex h-28 items-center justify-center">
      {[0, 1, 2].map((i) => (
        <motion.span
          key={i}
          className="absolute h-16 w-16 rounded-full border border-azure-soft/40"
          animate={{ scale: [1, 2.6], opacity: [0.6, 0] }}
          transition={{ duration: 3, repeat: Infinity, delay: i, ease: "easeOut" }}
        />
      ))}
      <span className="relative flex h-14 w-14 items-center justify-center rounded-2xl bg-azure/20 text-azure-soft shadow-[0_0_30px_rgba(62,142,247,0.5)]">
        <Bluetooth className="h-6 w-6" />
      </span>
    </div>
  );
}

function FlightVisual() {
  return (
    <div className="relative h-28">
      {[0, 1, 2].map((i) => (
        <motion.div
          key={i}
          className="absolute left-1/2 top-1/2 h-20 w-14 rounded-lg border border-white/50 bg-gradient-to-br from-white to-[#dfe9f7] shadow-xl"
          initial={{ x: "-50%", y: "-50%", rotate: 0 }}
          whileInView={{ x: `${-50 + (i - 1) * 70}%`, rotate: (i - 1) * 12, y: i === 1 ? "-60%" : "-45%" }}
          viewport={{ once: true }}
          transition={{ type: "spring", stiffness: 140, damping: 12, delay: 0.3 + i * 0.08 }}
        >
          <span className="absolute left-1.5 top-1 text-xs font-bold text-slate-900">{["Q", "K", "A"][i]}</span>
        </motion.div>
      ))}
      <div className="absolute inset-x-0 bottom-0 h-px bg-gradient-to-r from-transparent via-azure-cyan/60 to-transparent" />
    </div>
  );
}

function StatsVisual() {
  const bars = [
    { label: "大富豪", v: 0.62 },
    { label: "富豪", v: 0.48 },
    { label: "平民", v: 0.34 },
    { label: "貧民", v: 0.22 },
    { label: "大貧民", v: 0.14 },
  ];
  return (
    <div className="flex h-28 items-end gap-2.5" aria-hidden>
      {bars.map((b, i) => (
        <div key={b.label} className="flex flex-1 flex-col items-center gap-2">
          <motion.div
            className="w-full rounded-t-md bg-gradient-to-t from-azure/30 to-azure-soft"
            initial={{ height: 0 }}
            whileInView={{ height: `${b.v * 100}px` }}
            viewport={{ once: true }}
            transition={{ duration: 1, delay: 0.2 + i * 0.08, ease: [0.16, 1, 0.3, 1] }}
          />
          <span className="whitespace-nowrap text-[10px] text-ice-mute">{b.label}</span>
        </div>
      ))}
    </div>
  );
}

export default function BentoFeatures() {
  return (
    <section id="overview" className="relative mx-auto max-w-7xl px-4 py-28 sm:px-6 sm:py-36 lg:px-8">
      <Reveal className="mx-auto max-w-3xl text-center">
        <p className="eyebrow">Overview</p>
        <h2 className="section-title mt-4">
          深く、静かに。
          <br />
          <span className="text-gradient-x">すべてが卓のために。</span>
        </h2>
        <p className="mx-auto mt-6 max-w-xl text-pretty text-base leading-relaxed text-ice-mute sm:text-lg">
          ルールの自由度、通信の安全性、手触りのよい演出。大富豪を本気で遊ぶための要素を、ひとつのアプリに。
        </p>
      </Reveal>

      <div className="mt-16 grid auto-rows-auto grid-cols-1 gap-4 sm:mt-20 md:grid-cols-6 lg:gap-5">
        <BentoCard
          className="md:col-span-4 md:row-span-2"
          icon={<Layers className="h-4 w-4" />}
          eyebrow="Local Rules"
          title={
            <>
              約60種のローカルルールを、
              <br className="hidden sm:block" />
              自在にカスタマイズ。
            </>
          }
          visual={<RuleCloud />}
        >
          革命・縛り・場流し・割り込みから、都落ち・下剋上・天変地異まで。初心者〜完全上級者のプリセットに加え、
          自分だけのマイプリセットも保存できます。
        </BentoCard>

        <BentoCard
          className="md:col-span-2"
          delay={0.08}
          icon={<Wifi className="h-4 w-4" />}
          eyebrow="Offline Play"
          title="ルーター不要の近距離対戦。"
          visual={<SignalVisual />}
        >
          Bluetooth LE と LAN の両方に対応。
        </BentoCard>

        <BentoCard
          className="md:col-span-2"
          delay={0.12}
          icon={<Lock className="h-4 w-4" />}
          eyebrow="Security"
          title="全通信を、暗号化。"
        >
          ECDH (P-256) 鍵交換 ＋ AES-256-GCM。参加 PIN と 6 桁の確認コードで、卓を守ります。
        </BentoCard>

        <BentoCard
          className="md:col-span-3"
          delay={0.05}
          icon={<Sparkles className="h-4 w-4" />}
          eyebrow="60fps Motion"
          title="カードが、ばねのように舞う。"
          visual={<FlightVisual />}
        >
          Windows Composition API によるカード飛翔と物理ばね演出。場流れには Prism Flash / Aurora Sweep。
          ZIP のカードスキンパック、フルキーボード操作、ドラッグ＆ドロップ、観戦モードにも対応。
        </BentoCard>

        <BentoCard
          className="md:col-span-3"
          delay={0.1}
          icon={<BrainCircuit className="h-4 w-4" />}
          eyebrow="AI & Efficiency"
          title="考える AI と、徹底した省電力。"
        >
          未出カードを記憶し、終盤の勝ち筋を読む AI。完全イベント駆動、無音時のオーディオ自動停止、最小化・非アクティブ時のアニメーション停止で、
          バッテリー消費を極限まで抑えます。
        </BentoCard>

        <BentoCard
          className="md:col-span-6 lg:col-span-6"
          delay={0.05}
          icon={<BarChart3 className="h-4 w-4" />}
          eyebrow="Stats"
          title="すべての対局を、記録に。"
          visual={
            <div className="max-w-md">
              <StatsVisual />
            </div>
          }
        >
          ローカル SQLite データベースに、全対戦履歴・連勝記録・階級分布（大富豪〜大貧民）を保存します。
        </BentoCard>
      </div>
    </section>
  );
}
