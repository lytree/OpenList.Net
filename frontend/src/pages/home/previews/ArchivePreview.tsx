import { computed, defineComponent, h, ref, watch, type PropType } from "vue"
import {
  ElAlert,
  ElBreadcrumb,
  ElBreadcrumbItem,
  ElButton,
  ElEmpty,
  ElIcon,
  ElInput,
  ElMessage,
  ElOption,
  ElSelect,
  ElSkeleton,
  ElTag,
} from "element-plus"
import {
  ArrowLeft,
  Document,
  Folder,
  Headset,
  Picture,
  VideoCamera,
} from "@element-plus/icons-vue"
import { fsArchiveDecompress, fsArchiveList } from "~/api/fs"
import type { FsGetData, ObjType } from "~/api/types"
import { t } from "~/i18n"
import { buildArchiveTree, findArchiveNode, type ArchiveNode } from "~/utils/archive"
import { formatTime, humanSize } from "~/utils/format"
import { archiveInnerLink } from "~/utils/link"
import { pathBase, pathDir } from "~/utils/path"
import { guessObjType, resolvePreviews, type TypeExtMap } from "./registry"
import type { ArchiveListFn, PreviewChildProps } from "./types"

/**
 * 归档（zip/tar/7z/rar…）预览。
 *
 * 与旧前端的关键差异：**一次请求拿全量条目，前端自己建树**。
 * 后端 `/fs/archive/list` 不支持 `inner_path`，逐目录请求既慢又要处理隐式目录；
 * 而压缩包的条目列表本来就得扫一遍中央目录，一次给完反而更快。
 *
 * 单条目预览走 `/ae/{归档}?inner={条目}`（后端流式返回，支持 Range），
 * 所以图片/文本/音视频都能在归档里直接看，不必先解压。
 */
export default defineComponent({
  name: "ArchivePreview",
  props: {
    obj: { type: Object as PropType<PreviewChildProps["obj"]>, required: true },
    path: { type: String, required: true },
    src: { type: String, required: true },
    provider: { type: String, default: "" },
    /**
     * 数据源覆盖：分享页注入「带分享码」的请求实现。
     * 不传则走登录态 `/fs/archive/list`。
     */
    listArchive: {
      type: Function as PropType<ArchiveListFn>,
      default: undefined,
    },
    /**
     * 只读上下文（分享页）。归档内条目的内联预览走 `/ae`，需要登录态签名，
     * 游客拿不到，所以这里只列条目、不给点进去；解压也一并隐藏。
     */
    readOnly: { type: Boolean, default: false },
    /** 与主列表共用可配置扩展名（`audio_types` 等），供归档内条目判类型。 */
    typeExts: { type: Object as PropType<TypeExtMap>, default: () => ({}) },
  },
  setup(props) {
    const loading = ref(true)
    const error = ref("")
    /** 驱动的归档读取接口缺失（后端 501），与「解压失败」是两回事。 */
    const unsupported = ref(false)
    const tree = ref<ArchiveNode[]>([])

    const requiringPassword = ref(false)
    const password = ref("")
    const wrongPassword = ref(false)

    /** 当前所在目录（逐段路径），空数组即归档根。 */
    const dirPath = ref<string[]>([])
    /** 当前选中的文件（非空时右侧替换为内联预览）。 */
    const selected = ref<ArchiveNode | null>(null)
    const extracting = ref(false)

    async function load() {
      loading.value = true
      error.value = ""
      unsupported.value = false
      try {
        const resp = props.listArchive
          ? await props.listArchive(props.path, password.value)
          : await fsArchiveList(props.path, "", password.value)
        // 202 是 Go 上游「需要密码」的约定码；本仓库的 meta 门禁用业务码 403，
        // 两者都按「可能要密码」处理 —— 真输错了下面会给出明确提示。
        if (resp.code === 202 || resp.code === 403) {
          requiringPassword.value = true
          wrongPassword.value = password.value !== ""
          return
        }
        if (resp.code === 501) {
          unsupported.value = true
          error.value = resp.message
          return
        }
        if (resp.code !== 200) {
          error.value = resp.message || `HTTP ${resp.code}`
          return
        }
        tree.value = buildArchiveTree(resp.data?.content ?? [])
        requiringPassword.value = false
        wrongPassword.value = false
      } catch (e) {
        error.value = String((e as Error)?.message ?? e)
      } finally {
        loading.value = false
      }
    }

    watch(
      () => props.path,
      () => {
        dirPath.value = []
        selected.value = null
        password.value = ""
        void load()
      },
      { immediate: true },
    )

    const currentNodes = computed<ArchiveNode[]>(() => {
      if (dirPath.value.length === 0) return tree.value
      const node = findArchiveNode(tree.value, dirPath.value.join("/"))
      return node?.children ?? []
    })

    const crumbs = computed(() => [
      { name: ".", depth: 0 },
      ...dirPath.value.map((name, index) => ({ name, depth: index + 1 })),
    ])

    function openDir(node: ArchiveNode) {
      if (!node.isDir) {
        // 只读上下文（分享）没有 `/ae` 访问权，点开只会 401，所以干脆不选中。
        if (!props.readOnly) selected.value = node
        return
      }
      dirPath.value = node.path.split("/").filter(Boolean)
      selected.value = null
    }

    function jumpTo(depth: number) {
      dirPath.value = dirPath.value.slice(0, depth)
      selected.value = null
    }

    // ---- 归档内单文件的内联预览 ----------------------------------------
    const innerObj = computed<FsGetData | null>(() => {
      const node = selected.value
      if (!node) return null
      return {
        ...props.obj,
        name: node.name,
        size: node.size,
        is_dir: false,
        type: guessObjType(node.name, props.typeExts),
        modified: node.modified,
        raw_url: "",
        thumb: "",
      }
    })

    const innerSrc = computed(() =>
      selected.value
        ? archiveInnerLink(props.path, selected.value.path, {
            pass: password.value,
            sign: props.obj.sign,
          })
        : "",
    )

    /**
     * 归档内的预览器列表。
     *
     * 排掉 `archive`：条目路径对注册表来说是「另一个归档」，但它是当前归档
     * 内部的一条记录，路径语义不同，递归下去只会拼出 404 的链接。
     */
    const innerEntries = computed(() =>
      innerObj.value
        ? resolvePreviews(innerObj.value, {
            provider: props.provider,
            path: props.path,
            typeExts: props.typeExts,
          }).filter((entry) => entry.key !== "archive")
        : [],
    )
    const innerKey = ref("")
    const currentInner = computed(
      () => innerEntries.value.find((entry) => entry.key === innerKey.value) ?? innerEntries.value[0],
    )
    watch(selected, () => (innerKey.value = ""))

    async function extract() {
      extracting.value = true
      try {
        const resp = await fsArchiveDecompress({
          src_dir: pathDir(props.path),
          dst_dir: pathDir(props.path),
          name: [pathBase(props.path)],
          archive_pass: password.value,
          cache_full: true,
          put_into_new_dir: true,
          overwrite: false,
        })
        if (resp.code === 200) ElMessage.success(t("home.preview.archive_extract_hint"))
        else ElMessage.error(resp.message || `HTTP ${resp.code}`)
      } catch (e) {
        ElMessage.error(String((e as Error)?.message ?? e))
      } finally {
        extracting.value = false
      }
    }

    function nodeIcon(node: ArchiveNode) {
      if (node.isDir) return Folder
      const type: ObjType = guessObjType(node.name, props.typeExts)
      if (type === 5 /* ObjType.IMAGE */) return Picture
      if (type === 2 /* ObjType.VIDEO */) return VideoCamera
      if (type === 3 /* ObjType.AUDIO */) return Headset
      return Document
    }

    return () => (
      <div class="ol-archive-preview">
        <div class="ol-preview-toolbar ol-preview-toolbar-wrap">
          <ElBreadcrumb separator="/">
            {crumbs.value.map((crumb) => (
              <ElBreadcrumbItem>
                <span
                  class={
                    crumb.depth === dirPath.value.length && !selected.value
                      ? "ol-crumb ol-archive-crumb ol-crumb-current"
                      : "ol-crumb ol-archive-crumb"
                  }
                  onClick={() => jumpTo(crumb.depth)}
                >
                  {crumb.name}
                </span>
              </ElBreadcrumbItem>
            ))}
            {selected.value && (
              <ElBreadcrumbItem>
                <span class="ol-crumb ol-archive-crumb ol-crumb-current">{selected.value.name}</span>
              </ElBreadcrumbItem>
            )}
          </ElBreadcrumb>

          <div class="ol-preview-toolbar-right">
            {selected.value && (
              <ElButton size="small" onClick={() => (selected.value = null)}>
                <ElIcon style={{ marginRight: "4px" }}>
                  <ArrowLeft />
                </ElIcon>
                {t("home.preview.archive_back")}
              </ElButton>
            )}
            <ElTag size="small" type="info">
              {t("home.preview.archive_entries", { count: currentNodes.value.length })}
            </ElTag>
            {!props.readOnly && (
              <ElButton size="small" loading={extracting.value} onClick={extract}>
                {t("home.preview.archive_extract")}
              </ElButton>
            )}
          </div>
        </div>

        {requiringPassword.value ? (
          <div class="ol-archive-password">
            <ElAlert
              type={wrongPassword.value ? "error" : "info"}
              showIcon={true}
              closable={false}
              title={
                wrongPassword.value
                  ? t("home.preview.archive_wrong_password")
                  : t("home.preview.archive_encrypted")
              }
            />
            <div
              class="ol-dialog-row"
              onKeydown={(event: KeyboardEvent) => {
                if (event.key === "Enter") void load()
              }}
            >
              <ElInput
                modelValue={password.value}
                type="password"
                showPassword={true}
                placeholder={t("home.preview.archive_password")}
                onUpdate:modelValue={(value: string) => (password.value = value)}
              />
              <ElButton type="primary" onClick={load}>
                OK
              </ElButton>
            </div>
          </div>
        ) : loading.value ? (
          <div class="ol-archive-body">
            <ElSkeleton rows={8} animated={true} />
          </div>
        ) : error.value ? (
          <div class="ol-archive-body">
            <ElAlert
              type={unsupported.value ? "warning" : "error"}
              showIcon={true}
              closable={false}
              title={
                unsupported.value ? t("home.preview.archive_unsupported") : `Error: ${error.value}`
              }
              description={unsupported.value ? error.value : ""}
            />
          </div>
        ) : selected.value && currentInner.value ? (
          <div class="ol-archive-inner">
            {innerEntries.value.length > 1 && (
              <div class="ol-preview-toolbar">
                <ElSelect
                  modelValue={currentInner.value.key}
                  size="small"
                  style={{ width: "180px" }}
                  onUpdate:modelValue={(value: string | number | boolean) =>
                    (innerKey.value = String(value))
                  }
                >
                  {innerEntries.value.map((entry) => (
                    <ElOption key={entry.key} label={entry.name} value={entry.key} />
                  ))}
                </ElSelect>
              </div>
            )}
            {h(currentInner.value.component, {
              key: `${currentInner.value.key}:${selected.value.path}`,
              obj: innerObj.value,
              path: props.path,
              provider: props.provider,
              src: innerSrc.value,
              download_url: innerSrc.value,
            })}
          </div>
        ) : currentNodes.value.length === 0 ? (
          <div class="ol-archive-body">
            <ElEmpty description={t("home.preview.archive_empty")} />
          </div>
        ) : (
          <div class="ol-archive-list">
            {currentNodes.value.map((node) => (
              <div
                class="ol-archive-row"
                key={node.path}
                onClick={() => openDir(node)}
              >
                <ElIcon class="ol-archive-icon">
                  {h(nodeIcon(node))}
                </ElIcon>
                <span class="ol-archive-name" title={node.path}>
                  {node.name}
                </span>
                <span class="ol-archive-size">
                  {node.isDir ? "" : humanSize(node.size)}
                </span>
                <span class="ol-archive-time">
                  {node.modified && node.modified !== "0001-01-01T00:00:00+00:00"
                    ? formatTime(node.modified)
                    : ""}
                </span>
              </div>
            ))}
          </div>
        )}
      </div>
    )
  },
})
