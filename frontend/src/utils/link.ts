/**
 * 直链构造（对齐旧前端 `useLink().rawLink`）：
 * - 下载统一走 `/d{path}`：驱动可给直链时 302 到源站，否则服务器流式传输；
 * - 后端仅在需要签名时随 fs/list、fs/get 下发 `Obj.sign`，有则原样带上；
 * - 路径按段 encodeURIComponent，保留 `/` 分隔符。
 */
export function encodeFsPath(path: string): string {
  return path
    .split("/")
    .map((seg) => (seg ? encodeURIComponent(seg) : ""))
    .join("/")
}

export function downloadLink(path: string, sign?: string | null): string {
  const base = `${location.origin}/d${encodeFsPath(path)}`
  return sign ? `${base}?sign=${encodeURIComponent(sign)}` : base
}

/**
 * 预览用媒体源。
 *
 * 优先用 `fs/get` 下发的 `raw_url`（后端签名代理 `/p/{path}`，支持 Range，
 * 注释明确它是「前端视频/音频/pdf 预览的媒体源」）；后端未配置签名服务时
 * `raw_url` 为空，退回 `/d` 直链（驱动能给直链就 302，否则同样流式传输）。
 */
export function rawLink(
  path: string,
  rawUrl?: string | null,
  sign?: string | null,
): string {
  return rawUrl || downloadLink(path, sign)
}

/**
 * 归档内单个条目的访问地址：`/ae{归档路径}?inner={条目路径}`。
 *
 * 后端 `/ae/{path}` 在带 `inner` 时直接流式返回该条目（支持 Range），
 * 不带 `inner` 才是「解压到归档所在目录」。`pass` 对应加密归档；
 * `sign` 与下载直链同一套签名，缺了会在开启签名策略的站点上 401。
 */
export function archiveInnerLink(
  archivePath: string,
  innerPath: string,
  opts: { pass?: string; sign?: string | null } = {},
): string {
  const params = new URLSearchParams()
  params.set("inner", innerPath.startsWith("/") ? innerPath : `/${innerPath}`)
  if (opts.pass) params.set("pass", opts.pass)
  if (opts.sign) params.set("sign", opts.sign)
  return `${location.origin}/ae${encodeFsPath(archivePath)}?${params.toString()}`
}
