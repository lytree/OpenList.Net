import { computed, defineComponent, ref, watch, type PropType } from "vue"
import { ElAlert, ElButton, ElIcon, ElMessage, ElTag, ElTooltip } from "element-plus"
import { ArrowRight, CopyDocument, Document, Folder, Lightning } from "@element-plus/icons-vue"
import { useUserStore } from "~/stores/user"
import { buildArchiveTree, type ArchiveNode } from "~/utils/archive"
import { emitEvent } from "~/utils/bus"
import { formatTime, humanSize } from "~/utils/format"
import { parseTorrent, torrentMagnet, type ParsedTorrent } from "~/utils/torrent"
import { t } from "~/i18n"
import type { PreviewChildProps } from "./types"

/**
 * .torrent 预览。
 *
 * 解析全在浏览器里做（`utils/torrent`），不调后端 `/fs/torrent/parse`：
 * 那个端点要登录，而公开目录/分享页同样需要看种子内容；而且文件字节本来就在手上。
 *
 * 「离线下载」只是把磁力链接递给页面级的离线下载对话框 —— 下载器是谁、保存到哪儿、
 * 下完删不删，都是那个对话框的事，这里不该复制一份策略 UI。
 */

/** 超过这个体积不解析：正常种子几十 KB 到几 MB，几十 MB 的多半是异常文件。 */
const MAX_PARSE_BYTES = 32 * 1024 * 1024

export default defineComponent({
  name: "TorrentPreview",
  props: {
    obj: { type: Object as PropType<PreviewChildProps["obj"]>, required: true },
    path: { type: String, required: true },
    src: { type: String, required: true },
  },
  setup(props) {
    const user = useUserStore()
    const loading = ref(false)
    const error = ref("")
    const torrent = ref<ParsedTorrent | null>(null)
    /** 展开的目录路径集合（文件不参与）。 */
    const expanded = ref<Set<string>>(new Set())

    const canOfflineDownload = computed(() => user.can("offline_download"))

    const tree = computed<ArchiveNode[]>(() => {
      const parsed = torrent.value
      if (!parsed) return []
      // 复用归档的建树逻辑：种子里的路径同样是「相对路径 + 可能缺目录条目」
      return buildArchiveTree(
        parsed.files.map((file) => ({
          name: file.path,
          size: file.size,
          is_dir: false,
          modified: "",
        })),
      )
    })

    /** 目录的合计大小（`buildArchiveTree` 只填叶子，目录的 0 不是真值）。 */
    const dirSizes = computed(() => {
      const map = new Map<string, number>()
      const walk = (nodes: ArchiveNode[]): number => {
        let sum = 0
        for (const node of nodes) {
          if (node.isDir) {
            const inner = walk(node.children)
            map.set(node.path, inner)
            sum += inner
          } else {
            sum += node.size
          }
        }
        return sum
      }
      walk(tree.value)
      return map
    })

    async function load() {
      loading.value = true
      error.value = ""
      torrent.value = null
      try {
        if (props.obj.size > MAX_PARSE_BYTES) {
          throw new Error(
            `file is too large to parse in the browser (${humanSize(props.obj.size)})`,
          )
        }
        const resp = await fetch(props.src)
        if (!resp.ok) throw new Error(`${resp.status} ${resp.statusText}`)
        const bytes = new Uint8Array(await resp.arrayBuffer())
        const parsed = parseTorrent(bytes)
        torrent.value = parsed
        // 默认展开第一层目录：既能看清结构，又不会把几千个文件一次铺满。
        // `tree` 是 computed，赋值后已是最新值，不用再建一遍。
        expanded.value = new Set(
          tree.value.filter((node) => node.isDir).map((node) => node.path),
        )
      } catch (e) {
        error.value = String((e as Error)?.message ?? e)
      } finally {
        loading.value = false
      }
    }

    watch(() => props.src, () => void load(), { immediate: true })

    function toggle(node: ArchiveNode) {
      const next = new Set(expanded.value)
      if (next.has(node.path)) next.delete(node.path)
      else next.add(node.path)
      expanded.value = next
    }

    /**
     * 把树按当前展开状态拍平成「行 + 缩进层级」。
     *
     * 不写成递归渲染函数：一来避免 JSX 返回值在 `tsconfig` 里的类型纠缠，
     * 二来拍平后由 `expanded` 驱动，折叠/展开只是重算列表，不会重建整棵子树。
     */
    const visibleRows = computed(() => {
      const rows: { node: ArchiveNode; depth: number }[] = []
      const walk = (nodes: ArchiveNode[], depth: number) => {
        for (const node of nodes) {
          rows.push({ node, depth })
          if (node.isDir && expanded.value.has(node.path)) walk(node.children, depth + 1)
        }
      }
      walk(tree.value, 0)
      return rows
    })

    async function copyMagnet() {
      const parsed = torrent.value
      if (!parsed) return
      try {
        await navigator.clipboard.writeText(torrentMagnet(parsed))
        ElMessage.success(t("home.preview.torrent_copied"))
      } catch {
        ElMessage.error(t("home.preview.torrent_copy_failed"))
      }
    }

    function startOfflineDownload() {
      const parsed = torrent.value
      if (!parsed) return
      emitEvent("offline-download", { urls: [torrentMagnet(parsed)] })
    }

    return () => {
      if (loading.value) {
        return (
          <div class="ol-torrent-preview">
            <div class="ol-torrent-state">{t("home.preview.torrent_parsing")}</div>
          </div>
        )
      }

      if (error.value) {
        return (
          <div class="ol-torrent-preview">
            <div class="ol-torrent-state">
              <ElAlert
                type="error"
                showIcon={true}
                closable={false}
                title={t("home.preview.torrent_parse_failed")}
                description={error.value}
              />
            </div>
          </div>
        )
      }

      const parsed = torrent.value
      if (!parsed) return null

      return (
        <div class="ol-torrent-preview">
          <div class="ol-torrent-head">
            <div class="ol-torrent-title">{parsed.name}</div>
            <div class="ol-torrent-tags">
              <ElTag size="small" type="info">
                {humanSize(parsed.totalSize)}
              </ElTag>
              <ElTag size="small" type="info">
                {t("home.preview.torrent_files", { count: parsed.files.length })}
              </ElTag>
              {parsed.isPrivate && (
                <ElTag size="small" type="warning">
                  {t("home.preview.torrent_private")}
                </ElTag>
              )}
              {parsed.cas && (
                <ElTooltip
                  content={t("home.preview.torrent_cas_hint", { cloud: parsed.cas.cloud || "-" })}
                  placement="top"
                >
                  <ElTag size="small" type="success">
                    CAS
                  </ElTag>
                </ElTooltip>
              )}
            </div>
          </div>

          <div class="ol-torrent-meta">
            <div class="ol-torrent-meta-row">
              <span class="ol-torrent-meta-label">{t("home.preview.torrent_hash")}</span>
              <code class="ol-torrent-hash">{parsed.infoHash}</code>
            </div>
            <div class="ol-torrent-meta-row">
              <span class="ol-torrent-meta-label">{t("home.preview.torrent_pieces")}</span>
              <span>
                {parsed.pieceCount} × {humanSize(parsed.pieceLength)}
              </span>
            </div>
            <div class="ol-torrent-meta-row">
              <span class="ol-torrent-meta-label">{t("home.preview.torrent_trackers")}</span>
              <span>{parsed.trackers.length}</span>
            </div>
            {parsed.createdBy && (
              <div class="ol-torrent-meta-row">
                <span class="ol-torrent-meta-label">{t("home.preview.torrent_created_by")}</span>
                <span>{parsed.createdBy}</span>
              </div>
            )}
            {parsed.creationDate > 0 && (
              <div class="ol-torrent-meta-row">
                <span class="ol-torrent-meta-label">{t("home.preview.torrent_created_at")}</span>
                <span>{formatTime(new Date(parsed.creationDate * 1000).toISOString())}</span>
              </div>
            )}
            {parsed.comment && (
              <div class="ol-torrent-meta-row">
                <span class="ol-torrent-meta-label">{t("home.preview.torrent_comment")}</span>
                <span class="ol-torrent-comment">{parsed.comment}</span>
              </div>
            )}
          </div>

          <div class="ol-torrent-actions">
            <ElButton size="small" onClick={copyMagnet}>
              <ElIcon style={{ marginRight: "4px" }}>
                <CopyDocument />
              </ElIcon>
              {t("home.preview.torrent_copy_magnet")}
            </ElButton>
            {canOfflineDownload.value && (
              <ElButton size="small" type="primary" onClick={startOfflineDownload}>
                <ElIcon style={{ marginRight: "4px" }}>
                  <Lightning />
                </ElIcon>
                {t("home.preview.torrent_offline")}
              </ElButton>
            )}
          </div>

          <div class="ol-torrent-tree">
            {visibleRows.value.map(({ node, depth }) => {
              const open = expanded.value.has(node.path)
              return (
                <div
                  class="ol-torrent-row"
                  style={{ paddingLeft: `${8 + depth * 16}px` }}
                  onClick={() => node.isDir && toggle(node)}
                >
                  <ElIcon class={open ? "ol-torrent-caret is-open" : "ol-torrent-caret"}>
                    {node.isDir ? <ArrowRight /> : <span class="ol-torrent-caret-dot" />}
                  </ElIcon>
                  <ElIcon class="ol-torrent-icon">
                    {node.isDir ? <Folder /> : <Document />}
                  </ElIcon>
                  <span class="ol-torrent-name" title={node.path}>
                    {node.name}
                  </span>
                  <span class="ol-torrent-size">
                    {humanSize(node.isDir ? (dirSizes.value.get(node.path) ?? 0) : node.size)}
                  </span>
                </div>
              )
            })}
          </div>
        </div>
      )
    }
  },
})
