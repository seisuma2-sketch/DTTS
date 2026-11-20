import { motion } from "framer-motion";
import { Bookmark, Flame, Link2, RotateCcw, Scissors, Shuffle, Trophy } from "lucide-react";
import Reveal from "./Reveal";

const groups = [
  { icon: RotateCcw, title: "革命", items: ["革命", "階段革命", "クーデター", "オーメン", "ナナサン"] },
  { icon: Link2, title: "縛り", items: ["スート縛り", "数しば", "激しば", "片縛り"] },
  {
    icon: Scissors,
    title: "場流し・割り込み",
    items: ["8切り", "4止め", "砂嵐", "99車", "ろくろ首", "スペ3返し", "エンペラー"],
  },
  {
    icon: Shuffle,
    title: "順番・特殊効果",
    items: ["11バック（3種）", "5 / 13 スキップ", "9 / 12 リバース", "7渡し", "10捨て", "12ボンバー（ジョーカー宣言対応）"],
  },
  { icon: Trophy, title: "階級・局間", items: ["都落ち", "下剋上", "天変地異"] },
  { icon: Bookmark, title: "プリセット", items: ["初心者〜完全上級者プリセット", "マイプリセット保存"] },
];

/** スティッキー見出し＋スクロールで流れるルール一覧 */
export default function RulesShowcase() {
  return (
    <section id="rules" className="relative border-y border-white/[0.06] bg-abyss-950/40">
      <div className="mx-auto grid max-w-7xl gap-12 px-4 py-28 sm:px-6 sm:py-36 lg:grid-cols-[1fr_1.15fr] lg:gap-20 lg:px-8">
        <div className="lg:sticky lg:top-28 lg:self-start">
          <Reveal>
            <p className="eyebrow">Rules</p>
            <h2 className="section-title mt-4">
              あなたの地元の
              <br />
              大富豪が、ここに。
            </h2>
            <p className="mt-6 max-w-md text-pretty text-base leading-relaxed text-ice-mute sm:text-lg">
              約60のローカルルールを、ひとつずつ ON / OFF。友人との「いつものルール」を、そのまま卓に再現できます。
            </p>
            <div className="mt-10 flex items-baseline gap-3">
              <span className="text-gradient-x text-7xl font-extrabold tracking-tightest sm:text-8xl">60</span>
              <span className="text-sm text-ice-mute">
                種類前後の
                <br />
                ローカルルール
              </span>
            </div>
          </Reveal>
        </div>

        <ul className="space-y-4">
          {groups.map((g, i) => (
            <motion.li
              key={g.title}
              initial={{ opacity: 0, x: 40 }}
              whileInView={{ opacity: 1, x: 0 }}
              viewport={{ once: true, margin: "-60px" }}
              transition={{ duration: 0.8, delay: i * 0.04, ease: [0.16, 1, 0.3, 1] }}
              className="group rounded-3xl border border-white/10 bg-white/[0.03] p-6 backdrop-blur-xl transition hover:border-azure/40 sm:p-7"
            >
              <div className="flex items-center gap-3">
                <span className="flex h-10 w-10 items-center justify-center rounded-xl bg-azure/15 text-azure-soft transition group-hover:bg-azure/25">
                  <g.icon className="h-5 w-5" />
                </span>
                <h3 className="text-lg font-bold tracking-tight text-ice">{g.title}</h3>
                {g.title === "場流し・割り込み" && <Flame className="ml-auto h-4 w-4 text-azure-cyan/70" aria-hidden />}
              </div>
              <div className="mt-4 flex flex-wrap gap-2">
                {g.items.map((it) => (
                  <span key={it} className="rounded-full border border-white/10 bg-abyss-900/60 px-3 py-1.5 text-sm text-ice-body">
                    {it}
                  </span>
                ))}
              </div>
            </motion.li>
          ))}
        </ul>
      </div>
    </section>
  );
}
