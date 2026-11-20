export const PRODUCT = {
  name: "DTTS",
  fullName: "DTTS — 4-Player Daifugo Tabletop Suite",
  version: "v1.1.5",
  platform: "Windows 10 / 11 64bit",
} as const;

export type DownloadKind = "installer" | "zip";

export interface DownloadItem {
  kind: DownloadKind;
  fileName: string;
  href: string;
  label: string;
}

export const DOWNLOADS: Record<DownloadKind, DownloadItem> = {
  installer: {
    kind: "installer",
    fileName: "DTTS_Setup.exe",
    href: "/downloads/DTTS_Setup.exe",
    label: "インストーラー版",
  },
  zip: {
    kind: "zip",
    fileName: "DTTS_v1.1.5_x64.zip",
    href: "/downloads/DTTS_v1.1.5_x64.zip",
    label: "ZIP 版",
  },
};

export const NAV_ITEMS = [
  { id: "overview", label: "概要" },
  { id: "rules", label: "ルール" },
  { id: "multiplayer", label: "通信対戦" },
  { id: "download", label: "ダウンロード" },
] as const;
