import { useState } from "react";
import { AnimatePresence, motion } from "framer-motion";
import { Plus } from "lucide-react";
import Reveal from "./Reveal";

const faqs = [
  {
    q: "インターネット接続は必要ですか？",
    a: "不要です。NPC との対戦はもちろん、通信対戦も Bluetooth LE による近距離オフライン通信、または同じ LAN 内の TCP / UDP 通信で行います。Wi-Fi やルーターがなくても、Bluetooth LE で対戦できます。",
  },
  {
    q: "管理者権限や証明書は必要ですか？",
    a: "インストーラー版（DTTS_Setup.exe）は、管理者権限もテスト証明書も不要です。ダブルクリックで導入でき、デスクトップの「DTTS」からすぐに起動できます。",
  },
  {
    q: "BLE 親機（ホスト）に必要な条件は？",
    a: "Bluetooth がオンになっており、PC の Bluetooth アダプターが Bluetooth LE と Peripheral（ホスト）機能に対応している必要があります。非対応のアダプターではホストになれないため、その場合は別の PC がホストになるか、LAN 対戦をご利用ください。",
  },
  {
    q: "LAN 対戦でホストになるときの注意点は？",
    a: "初めてホストになるとき、Windows Defender ファイアウォールの許可を求められることがあります。プライベート ネットワークを許可すると、同じ LAN 内の PC からロビーで部屋が見えるようになります。",
  },
  {
    q: "対戦中に通信が切れたらどうなりますか？",
    a: "切断したプレイヤーの席は即座に NPC が引き継ぎ、対局はそのまま続きます。再接続すれば元の席に復帰できます。",
  },
  {
    q: "他のプレイヤーに手札を見られる心配はありませんか？",
    a: "ホスト権威型アーキテクチャにより、他人の手札の情報はあなたの端末に送られません。通信は ECDH (P-256) 鍵交換と AES-256-GCM で暗号化され、6 桁の確認コード（SAS）で中間者攻撃を検知できます。",
  },
  {
    q: "戦績はどこに保存されますか？",
    a: "PC 内のローカル SQLite データベースに、全対戦履歴・連勝記録・階級分布（大富豪〜大貧民）が記録されます。",
  },
];

function Item({ q, a, open, onToggle, idx }: { q: string; a: string; open: boolean; onToggle: () => void; idx: number }) {
  const id = `faq-${idx}`;
  return (
    <div className="border-b border-white/[0.08]">
      <h3>
        <button
          onClick={onToggle}
          aria-expanded={open}
          aria-controls={id}
          className="flex w-full items-center justify-between gap-6 py-6 text-left text-base font-semibold text-ice transition hover:text-azure-soft sm:text-lg"
        >
          {q}
          <motion.span
            animate={{ rotate: open ? 45 : 0 }}
            transition={{ type: "spring", stiffness: 300, damping: 22 }}
            className={`flex h-8 w-8 shrink-0 items-center justify-center rounded-full border transition ${
              open ? "border-azure/60 bg-azure/15 text-azure-soft" : "border-white/15 text-ice-mute"
            }`}
          >
            <Plus className="h-4 w-4" />
          </motion.span>
        </button>
      </h3>
      <AnimatePresence initial={false}>
        {open && (
          <motion.div
            id={id}
            role="region"
            initial={{ height: 0, opacity: 0 }}
            animate={{ height: "auto", opacity: 1 }}
            exit={{ height: 0, opacity: 0 }}
            transition={{ duration: 0.4, ease: [0.16, 1, 0.3, 1] }}
            className="overflow-hidden"
          >
            <p className="max-w-3xl pb-6 text-[15px] sm:pr-12 leading-relaxed text-ice-mute">{a}</p>
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}

export default function Faq() {
  const [open, setOpen] = useState<number | null>(0);
  return (
    <section id="faq" className="relative mx-auto max-w-4xl px-4 py-28 sm:px-6 sm:py-36">
      <Reveal className="text-center">
        <p className="eyebrow">FAQ</p>
        <h2 className="section-title mt-4">よくある質問</h2>
      </Reveal>
      <Reveal delay={0.1} className="mt-14 border-t border-white/[0.08]">
        {faqs.map((f, i) => (
          <Item key={f.q} idx={i} q={f.q} a={f.a} open={open === i} onToggle={() => setOpen(open === i ? null : i)} />
        ))}
      </Reveal>
    </section>
  );
}
