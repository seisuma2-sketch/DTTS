import { Archive, BatteryCharging, Check, Cpu, Download, MonitorSmartphone, PackageCheck, PlayCircle, ShieldCheck, Sparkles, Wrench } from "lucide-react";
import Reveal from "./Reveal";
import { useDownload } from "./DownloadContext";
import { DOWNLOADS, PRODUCT, type DownloadKind } from "../data/product";

const options: {
  kind: DownloadKind;
  recommended: boolean;
  title: string;
  desc: string;
  bullets: string[];
}[] = [
  {
    kind: "installer",
    recommended: true,
    title: "インストーラー版",
    desc: "いちばん簡単。ダブルクリックするだけで導入できます。",
    bullets: ["管理者権限・テスト証明書は不要", "デスクトップに「DTTS」を作成", "迷わず始めたい方に"],
  },
  {
    kind: "zip",
    recommended: false,
    title: "ZIP 版",
    desc: "展開するだけ。好きなフォルダーに置いて使えます。",
    bullets: ["インストール操作なし", "配置場所を自分で決められる", "手動で管理したい方に"],
  },
];

const steps = [
  { icon: Download, title: "ダウンロード", body: "DTTS_Setup.exe を保存します。" },
  { icon: PackageCheck, title: "実行", body: "ダブルクリックで導入。権限の昇格は求められません。" },
  { icon: PlayCircle, title: "対戦開始", body: "デスクトップの「DTTS」から、すぐに卓へ。" },
];

const notes = [
  {
    icon: Wrench,
    title: "安定性",
    body: "切断時の NPC 席引き継ぎと再接続による復帰で、対局が途切れません。",
  },
  {
    icon: BatteryCharging,
    title: "省電力",
    body: "完全イベント駆動、無音時のオーディオ自動停止、最小化・非アクティブ時のアニメーション停止。",
  },
  {
    icon: ShieldCheck,
    title: "セキュリティ",
    body: "ECDH (P-256) ＋ AES-256-GCM の全通信暗号化、参加 PIN ロック、6 桁の確認コード（SAS）。",
  },
];

export default function DownloadSection() {
  const { startDownload } = useDownload();

  return (
    <section id="download" className="relative border-t border-white/[0.06] bg-gradient-to-b from-abyss-950/30 to-abyss-950/80 py-28 sm:py-36">
      <div className="mx-auto max-w-7xl px-4 sm:px-6 lg:px-8">
        <Reveal className="mx-auto max-w-3xl text-center">
          <p className="eyebrow">Download</p>
          <h2 className="section-title mt-4">
            今夜の卓は、
            <br />
            <span className="text-gradient-x">数秒で準備できる。</span>
          </h2>
          <p className="mt-6 text-base text-ice-mute sm:text-lg">
            {PRODUCT.fullName} · <span className="font-mono text-ice-body">{PRODUCT.version}</span> · 無料
          </p>
        </Reveal>

        {/* 比較カード */}
        <div className="mx-auto mt-16 grid max-w-5xl gap-5 md:grid-cols-2">
          {options.map((o, i) => {
            const file = DOWNLOADS[o.kind];
            return (
              <Reveal key={o.kind} delay={i * 0.08}>
                <div
                  className={`relative flex h-full flex-col rounded-[28px] border p-7 backdrop-blur-xl transition sm:p-9 ${
                    o.recommended
                      ? "border-azure/50 bg-azure/[0.07] shadow-[0_30px_100px_-30px_rgba(62,142,247,0.6)]"
                      : "border-white/10 bg-white/[0.03] hover:border-white/20"
                  }`}
                >
                  {o.recommended && (
                    <span className="absolute -top-3 left-7 rounded-full bg-azure px-3 py-1 text-[11px] font-semibold text-white shadow-lg shadow-azure/40">
                      推奨
                    </span>
                  )}
                  <div className="flex items-center gap-3">
                    {o.kind === "installer" ? (
                      <Sparkles className="h-5 w-5 text-azure-soft" />
                    ) : (
                      <Archive className="h-5 w-5 text-azure-soft" />
                    )}
                    <h3 className="text-xl font-bold tracking-tight text-ice">{o.title}</h3>
                  </div>
                  <p className="mt-2 break-all font-mono text-sm text-ice-mute">{file.fileName}</p>
                  <p className="mt-4 text-[15px] leading-relaxed text-ice-body">{o.desc}</p>
                  <ul className="mt-6 space-y-2.5">
                    {o.bullets.map((b) => (
                      <li key={b} className="flex items-start gap-2.5 text-sm text-ice-body">
                        <Check className="mt-0.5 h-4 w-4 shrink-0 text-azure-cyan" />
                        {b}
                      </li>
                    ))}
                  </ul>
                  <div className="mt-auto pt-8">
                    <button
                      onClick={() => startDownload(o.kind)}
                      className={`${o.recommended ? "btn-primary" : "btn-ghost"} w-full`}
                    >
                      <Download className="h-4 w-4" />
                      {file.fileName} をダウンロード
                    </button>
                    <a
                      href={file.href}
                      className="mt-3 block text-center font-mono text-[11px] text-ice-mute/70 underline-offset-4 hover:text-ice-mute hover:underline"
                    >
                      {file.href}
                    </a>
                  </div>
                </div>
              </Reveal>
            );
          })}
        </div>

        {/* 導入ステップ */}
        <div className="mx-auto mt-24 max-w-5xl">
          <Reveal>
            <h3 className="text-center text-2xl font-bold tracking-tight text-ice sm:text-3xl">導入は、3 ステップ。</h3>
          </Reveal>
          <ol className="relative mt-12 grid gap-5 md:grid-cols-3">
            <div className="absolute left-[16%] right-[16%] top-7 hidden h-px bg-gradient-to-r from-azure/0 via-azure/50 to-azure/0 md:block" aria-hidden />
            {steps.map((s, i) => (
              <Reveal key={s.title} delay={i * 0.1}>
                <li className="relative flex flex-col items-center text-center">
                  <span className="relative flex h-14 w-14 items-center justify-center rounded-2xl border border-azure/40 bg-abyss-900 text-azure-soft shadow-[0_0_30px_-6px_rgba(62,142,247,0.7)]">
                    <s.icon className="h-6 w-6" />
                  </span>
                  <p className="mt-5 font-mono text-xs text-azure-soft">STEP {i + 1}</p>
                  <p className="mt-1 text-lg font-semibold text-ice">{s.title}</p>
                  <p className="mt-2 max-w-[16rem] text-sm leading-relaxed text-ice-mute">{s.body}</p>
                </li>
              </Reveal>
            ))}
          </ol>
        </div>

        {/* 動作環境 + リリースノート */}
        <div className="mx-auto mt-24 grid max-w-5xl gap-5 lg:grid-cols-[0.8fr_1.2fr]">
          <Reveal>
            <div className="glass h-full rounded-[28px] p-7 sm:p-8">
              <p className="eyebrow">System Requirements</p>
              <h3 className="mt-3 text-xl font-bold tracking-tight text-ice">動作環境</h3>
              <dl className="mt-6 space-y-5">
                <div className="flex gap-4">
                  <MonitorSmartphone className="mt-0.5 h-5 w-5 shrink-0 text-azure-soft" />
                  <div>
                    <dt className="text-xs text-ice-mute">OS</dt>
                    <dd className="mt-0.5 text-sm text-ice">Windows 10 1809 以降 / Windows 11</dd>
                  </div>
                </div>
                <div className="flex gap-4">
                  <Cpu className="mt-0.5 h-5 w-5 shrink-0 text-azure-soft" />
                  <div>
                    <dt className="text-xs text-ice-mute">アーキテクチャ</dt>
                    <dd className="mt-0.5 text-sm text-ice">x64（64bit）</dd>
                  </div>
                </div>
              </dl>
            </div>
          </Reveal>
          <Reveal delay={0.08}>
            <div className="glass h-full rounded-[28px] p-7 sm:p-8">
              <div className="flex items-center justify-between">
                <p className="eyebrow">Release Notes</p>
                <span className="rounded-full border border-azure/40 bg-azure/10 px-2.5 py-0.5 font-mono text-xs text-azure-soft">
                  {PRODUCT.version}
                </span>
              </div>
              <h3 className="mt-3 text-xl font-bold tracking-tight text-ice">{PRODUCT.version} の強化ポイント</h3>
              <ul className="mt-6 space-y-5">
                {notes.map((n) => (
                  <li key={n.title} className="flex gap-4">
                    <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-xl bg-azure/15 text-azure-soft">
                      <n.icon className="h-4 w-4" />
                    </span>
                    <div>
                      <p className="text-sm font-semibold text-ice">{n.title}</p>
                      <p className="mt-1 text-sm leading-relaxed text-ice-mute">{n.body}</p>
                    </div>
                  </li>
                ))}
              </ul>
            </div>
          </Reveal>
        </div>
      </div>
    </section>
  );
}
