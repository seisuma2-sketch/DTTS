import { useRef } from "react";
import { motion, useScroll, useTransform, type Variants } from "framer-motion";
import { Bluetooth, EyeOff, KeyRound, Laptop, Network, RefreshCw, ShieldCheck, UserCheck } from "lucide-react";
import Reveal from "./Reveal";

const peers = [
  { x: "15%", y: "14%", label: "Guest A" },
  { x: "85%", y: "18%", label: "Guest B" },
  { x: "50%", y: "90%", label: "Guest C" },
];

const draw: Variants = {
  hidden: { opacity: 0 },
  show: (i: number) => ({ opacity: 1, transition: { duration: 1.2, delay: 0.3 + i * 0.2 } }),
};

function TopologyVisual() {
  return (
    <div className="relative mx-auto aspect-square w-full max-w-[440px]">
      <svg className="absolute inset-0 h-full w-full" viewBox="0 0 100 100" aria-hidden>
        <defs>
          <linearGradient id="link" x1="0" x2="1">
            <stop offset="0" stopColor="#22D3EE" stopOpacity="0.1" />
            <stop offset="0.5" stopColor="#60A5FA" stopOpacity="0.8" />
            <stop offset="1" stopColor="#22D3EE" stopOpacity="0.1" />
          </linearGradient>
        </defs>
        {peers.map((p, i) => (
          <motion.path
            key={i}
            d={`M50 50 L${parseFloat(p.x)} ${parseFloat(p.y)}`}
            fill="none"
            stroke="url(#link)"
            strokeWidth="0.5"
            strokeDasharray="2 1.5"
            variants={draw}
            custom={i}
            initial="hidden"
            whileInView="show"
            viewport={{ once: true }}
          />
        ))}
      </svg>
      {/* 暗号パケット */}
      {peers.map((p, i) => (
        <motion.span
          key={`pkt-${i}`}
          className="absolute h-2 w-2 -translate-x-1/2 -translate-y-1/2 rounded-full bg-azure-cyan shadow-[0_0_12px_#22D3EE]"
          animate={{ left: ["50%", p.x, "50%"], top: ["50%", p.y, "50%"] }}
          transition={{ duration: 4, repeat: Infinity, delay: i * 1.2, ease: "easeInOut" }}
        />
      ))}
      {peers.map((p) => (
        <div key={p.label} className="absolute -translate-x-1/2 -translate-y-1/2" style={{ left: p.x, top: p.y }}>
          <div className="glass flex flex-col items-center gap-1 rounded-2xl px-3 py-2.5">
            <Laptop className="h-5 w-5 text-ice-body" />
            <span className="whitespace-nowrap text-[10px] text-ice-mute">{p.label}</span>
          </div>
        </div>
      ))}
      {/* ホスト */}
      <div className="absolute left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2">
        {[0, 1].map((i) => (
          <motion.span
            key={i}
            className="absolute inset-0 rounded-3xl border border-azure/50"
            animate={{ scale: [1, 1.6], opacity: [0.7, 0] }}
            transition={{ duration: 2.8, repeat: Infinity, delay: i * 1.4 }}
          />
        ))}
        <div className="relative flex flex-col items-center gap-1 rounded-3xl border border-azure/50 bg-abyss-800/90 px-4 py-3 sm:px-5 sm:py-4 shadow-[0_0_60px_-10px_rgba(62,142,247,0.8)] backdrop-blur-xl">
          <ShieldCheck className="h-7 w-7 text-azure-soft" />
          <span className="text-xs font-semibold text-ice">Host</span>
          <span className="text-[9px] text-ice-mute">権威サーバー</span>
        </div>
      </div>
    </div>
  );
}

const sas = ["4", "8", "2", "9", "1", "7"];

function SasCard() {
  return (
    <div className="glass rounded-3xl p-6 sm:p-7">
      <div className="flex items-center justify-between">
        <p className="eyebrow">確認コード（SAS）</p>
        <span className="text-[10px] text-ice-mute">表示例</span>
      </div>
      <div className="mt-5 grid grid-cols-6 gap-1.5 sm:gap-2">
        {sas.map((d, i) => (
          <motion.span
            key={i}
            initial={{ rotateX: 90, opacity: 0 }}
            whileInView={{ rotateX: 0, opacity: 1 }}
            viewport={{ once: true }}
            transition={{ delay: 0.3 + i * 0.08, type: "spring", stiffness: 200, damping: 16 }}
            className={`flex aspect-[3/4] items-center justify-center rounded-xl border border-white/10 bg-abyss-900/80 font-mono text-2xl font-bold text-ice sm:text-3xl`}
          >
            {d}
          </motion.span>
        ))}
      </div>
      <p className="mt-5 text-sm leading-relaxed text-ice-mute">
        ホストとゲストの画面に同じ 6 桁が表示されることを確かめるだけ。中間者攻撃をその場で見抜けます。
      </p>
    </div>
  );
}

const points = [
  { icon: Bluetooth, title: "Bluetooth LE (GATT)", body: "Wi-Fi もルーターも不要。手元の PC 同士で、すぐに卓を囲めます。" },
  { icon: Network, title: "TCP / UDP LAN", body: "同じネットワーク内なら LAN 対戦も。ロビーから部屋を見つけて参加。" },
  { icon: RefreshCw, title: "切断に強い", body: "切断時は即座に NPC が席を引き継ぎ、再接続で元の席に復帰できます。" },
  { icon: KeyRound, title: "ECDH P-256 ＋ AES-256-GCM", body: "鍵交換から全通信の暗号化まで、標準的で堅牢な暗号方式を採用。" },
  { icon: UserCheck, title: "参加 PIN ロック", body: "PIN を知る人だけが卓に着けます。" },
  { icon: EyeOff, title: "手札の秘匿", body: "ホスト権威型アーキテクチャで、他人の手札はあなたの端末に届きません。" },
];

export default function SecuritySection() {
  const ref = useRef<HTMLElement>(null);
  const { scrollYProgress } = useScroll({ target: ref, offset: ["start end", "end start"] });
  const glowY = useTransform(scrollYProgress, [0, 1], ["-20%", "30%"]);

  return (
    <section id="multiplayer" ref={ref} className="relative overflow-hidden py-28 sm:py-36">
      <motion.div
        style={{ y: glowY }}
        className="pointer-events-none absolute left-1/2 top-0 h-[600px] w-[900px] -translate-x-1/2 rounded-full bg-azure/[0.12] blur-[140px]"
      />
      <div className="relative mx-auto max-w-7xl px-4 sm:px-6 lg:px-8">
        <Reveal className="mx-auto max-w-3xl text-center">
          <p className="eyebrow">Multiplayer & Security</p>
          <h2 className="section-title mt-4">
            近くの誰かと、
            <br />
            <span className="text-gradient-x">安心して卓を囲む。</span>
          </h2>
          <p className="mx-auto mt-6 max-w-xl text-pretty text-base leading-relaxed text-ice-mute sm:text-lg">
            インターネットを経由しない近距離オフライン対戦。そのうえで、通信はすべて暗号化されています。
          </p>
        </Reveal>

        <div className="mt-16 grid items-center gap-10 lg:mt-24 lg:grid-cols-2 lg:gap-16">
          <Reveal>
            <TopologyVisual />
          </Reveal>
          <div className="space-y-5">
            <Reveal delay={0.1}>
              <SasCard />
            </Reveal>
            <Reveal delay={0.15}>
              <div className="glass flex flex-wrap items-center gap-x-6 gap-y-3 rounded-3xl p-6 font-mono text-xs text-ice-body sm:text-sm">
                <span className="flex items-center gap-2">
                  <span className="h-1.5 w-1.5 rounded-full bg-azure-cyan" />
                  ECDH P-256
                </span>
                <span className="text-ice-mute">→</span>
                <span className="flex items-center gap-2">
                  <span className="h-1.5 w-1.5 rounded-full bg-azure-soft" />
                  AES-256-GCM
                </span>
                <span className="text-ice-mute">→</span>
                <span className="flex items-center gap-2">
                  <span className="h-1.5 w-1.5 rounded-full bg-azure" />
                  SAS 6 桁照合
                </span>
              </div>
            </Reveal>
          </div>
        </div>

        <div className="mt-16 grid gap-4 sm:grid-cols-2 lg:mt-24 lg:grid-cols-3">
          {points.map((p, i) => (
            <Reveal key={p.title} delay={i * 0.05}>
              <div className="h-full rounded-3xl border border-white/[0.08] bg-white/[0.02] p-6 transition hover:border-azure/40 hover:bg-white/[0.04]">
                <p.icon className="h-5 w-5 text-azure-soft" />
                <h3 className="mt-4 font-semibold text-ice">{p.title}</h3>
                <p className="mt-2 text-sm leading-relaxed text-ice-mute">{p.body}</p>
              </div>
            </Reveal>
          ))}
        </div>
      </div>
    </section>
  );
}
