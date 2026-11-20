import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from "react";
import { AnimatePresence, motion } from "framer-motion";
import { AlertCircle, CheckCircle2, Download, FolderOpen, Monitor, MousePointerClick, PlayCircle, X } from "lucide-react";
import { DOWNLOADS, type DownloadItem, type DownloadKind } from "../data/product";

type ToastState = { id: number; item: DownloadItem; status: "started" | "missing" } | null;

const Ctx = createContext<{ startDownload: (kind: DownloadKind) => void } | null>(null);

export function useDownload() {
  const ctx = useContext(Ctx);
  if (!ctx) throw new Error("useDownload must be used inside DownloadProvider");
  return ctx;
}

/** 配布ファイルの存在確認（未配置時に SPA フォールバックの index.html を誤って保存させないため） */
async function isFileAvailable(href: string) {
  try {
    const res = await fetch(href, { method: "HEAD", cache: "no-store" });
    const type = res.headers.get("content-type") ?? "";
    return res.ok && !type.includes("text/html");
  } catch {
    return false;
  }
}

export function DownloadProvider({ children }: { children: ReactNode }) {
  const [toast, setToast] = useState<ToastState>(null);
  const [guide, setGuide] = useState<DownloadItem | null>(null);
  const timer = useRef<number | undefined>(undefined);

  const startDownload = useCallback(async (kind: DownloadKind) => {
    const item = DOWNLOADS[kind];
    const available = await isFileAvailable(item.href);
    if (available) {
      const a = document.createElement("a");
      a.href = item.href;
      a.download = item.fileName;
      document.body.appendChild(a);
      a.click();
      a.remove();
    }
    window.clearTimeout(timer.current);
    setToast({ id: Date.now(), item, status: available ? "started" : "missing" });
    timer.current = window.setTimeout(() => setToast(null), 6000);
    setGuide(item);
  }, []);

  const closeGuide = useCallback(() => setGuide(null), []);
  useEffect(() => () => window.clearTimeout(timer.current), []);

  return (
    <Ctx.Provider value={{ startDownload }}>
      {children}
      <InstallGuideModal item={guide} onClose={closeGuide} />
      <Toast toast={toast} onClose={() => setToast(null)} />
    </Ctx.Provider>
  );
}

function Toast({ toast, onClose }: { toast: ToastState; onClose: () => void }) {
  return (
    <div className="pointer-events-none fixed inset-x-0 bottom-4 z-[70] flex justify-center px-4 sm:bottom-6 sm:justify-end sm:px-6">
      <AnimatePresence>
        {toast && (
          <motion.div
            key={toast.id}
            role="status"
            initial={{ opacity: 0, y: 24, scale: 0.96 }}
            animate={{ opacity: 1, y: 0, scale: 1 }}
            exit={{ opacity: 0, y: 12, scale: 0.98 }}
            transition={{ type: "spring", stiffness: 380, damping: 30 }}
            className="glass pointer-events-auto flex w-full max-w-sm items-start gap-3 rounded-2xl !bg-abyss-900/85 p-4 shadow-2xl shadow-black/50"
          >
            {toast.status === "started" ? (
              <CheckCircle2 className="mt-0.5 h-5 w-5 shrink-0 text-azure-cyan" />
            ) : (
              <AlertCircle className="mt-0.5 h-5 w-5 shrink-0 text-amber-300" />
            )}
            <div className="min-w-0 flex-1">
              <p className="text-sm font-semibold text-ice">
                {toast.status === "started" ? "ダウンロードを開始しました" : "配布ファイルが見つかりません"}
              </p>
              <p className="mt-0.5 truncate font-mono text-xs text-ice-mute">{toast.item.href}</p>
              {toast.status === "missing" && (
                <p className="mt-1 text-xs text-ice-mute">サーバーにファイルが未配置です。時間をおいて再度お試しください。</p>
              )}
              <div className="mt-3 h-0.5 overflow-hidden rounded-full bg-white/10">
                <motion.div
                  className="h-full bg-gradient-to-r from-azure-cyan to-azure"
                  initial={{ width: "0%" }}
                  animate={{ width: "100%" }}
                  transition={{ duration: 6, ease: "linear" }}
                />
              </div>
            </div>
            <button onClick={onClose} className="rounded-md p-1 text-ice-mute hover:text-ice" aria-label="通知を閉じる">
              <X className="h-4 w-4" />
            </button>
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}

function InstallGuideModal({ item, onClose }: { item: DownloadItem | null; onClose: () => void }) {
  useEffect(() => {
    if (!item) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    const prev = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      window.removeEventListener("keydown", onKey);
      document.body.style.overflow = prev;
    };
  }, [item, onClose]);

  const steps =
    item?.kind === "zip"
      ? [
          { icon: Download, title: "ダウンロード", body: `${item.fileName} を保存します。` },
          { icon: FolderOpen, title: "展開して実行", body: "任意のフォルダーへ展開し、中のアプリを起動します。" },
          { icon: PlayCircle, title: "対戦開始", body: "卓に着いたら、そのまま対戦を始められます。" },
        ]
      : [
          { icon: Download, title: "ダウンロード", body: "DTTS_Setup.exe を保存します。" },
          { icon: MousePointerClick, title: "実行", body: "ダブルクリックで導入。管理者権限・テスト証明書は不要です。" },
          { icon: PlayCircle, title: "対戦開始", body: "デスクトップの「DTTS」から、すぐに対戦を始められます。" },
        ];

  return (
    <AnimatePresence>
      {item && (
        <motion.div
          className="fixed inset-0 z-[60] flex items-end justify-center p-4 sm:items-center"
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
          exit={{ opacity: 0 }}
        >
          <div className="absolute inset-0 bg-abyss-950/70 backdrop-blur-md" onClick={onClose} />
          <motion.div
            role="dialog"
            aria-modal="true"
            aria-labelledby="guide-title"
            initial={{ opacity: 0, y: 40, scale: 0.97 }}
            animate={{ opacity: 1, y: 0, scale: 1 }}
            exit={{ opacity: 0, y: 24, scale: 0.98 }}
            transition={{ type: "spring", stiffness: 300, damping: 30 }}
            className="glass relative w-full max-w-lg overflow-hidden rounded-3xl !bg-abyss-900/90 p-6 shadow-2xl shadow-black/60 sm:p-8"
          >
            <div className="pointer-events-none absolute -top-24 left-1/2 h-48 w-80 -translate-x-1/2 rounded-full bg-azure/25 blur-3xl" />
            <button
              onClick={onClose}
              autoFocus
              className="absolute right-4 top-4 rounded-full p-2 text-ice-mute transition hover:bg-white/5 hover:text-ice"
              aria-label="閉じる"
            >
              <X className="h-5 w-5" />
            </button>
            <p className="eyebrow relative">導入ガイド · {item.label}</p>
            <h3 id="guide-title" className="relative mt-2 text-2xl font-bold tracking-tight text-ice">
              3 ステップで、卓に着く。
            </h3>
            <ol className="relative mt-6 space-y-3">
              {steps.map((s, i) => (
                <motion.li
                  key={s.title}
                  initial={{ opacity: 0, x: -12 }}
                  animate={{ opacity: 1, x: 0 }}
                  transition={{ delay: 0.12 + i * 0.08 }}
                  className="flex gap-4 rounded-2xl border border-white/[0.07] bg-white/[0.03] p-4"
                >
                  <span className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-azure/15 text-azure-soft">
                    <s.icon className="h-5 w-5" />
                  </span>
                  <div>
                    <p className="text-sm font-semibold text-ice">
                      <span className="mr-2 font-mono text-azure-soft">{i + 1}.</span>
                      {s.title}
                    </p>
                    <p className="mt-0.5 text-sm text-ice-mute">{s.body}</p>
                  </div>
                </motion.li>
              ))}
            </ol>
            <div className="relative mt-6 flex items-center gap-2 text-xs text-ice-mute">
              <Monitor className="h-4 w-4" />
              Windows 10 1809 以降 / Windows 11（x64）
            </div>
            <div className="relative mt-6 flex flex-col gap-3 sm:flex-row">
              <a href={item.href} download={item.fileName} className="btn-ghost flex-1 !py-3 text-sm">
                <Download className="h-4 w-4" />
                直接リンクから保存
              </a>
              <button onClick={onClose} className="btn-primary flex-1 !py-3 text-sm">
                閉じる
              </button>
            </div>
          </motion.div>
        </motion.div>
      )}
    </AnimatePresence>
  );
}
