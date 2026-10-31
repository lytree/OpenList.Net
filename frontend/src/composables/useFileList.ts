import { computed, ref, watch } from "vue"
import { useRoute, useRouter } from "vue-router"
import { fsGet, fsList } from "~/api/fs"
import { ObjType, type FsGetData, type Obj } from "~/api/types"
import { useSettingsStore } from "~/stores/settings"
import { filterObjs, paginate, PAGE_SIZES } from "~/utils/list"
import { standardizePath } from "~/utils/path"

export type SortKey = "name" | "size" | "modified"
export type SortOrder = "asc" | "desc"

/** 目录状态机：一次进入路径只有这四种结局，UI 按状态渲染即可。 */
export type ViewState = "loading" | "folder" | "file" | "need-password" | "error"

/** 把站点设置里的每页条数收敛到 UI 能表达的取值域。 */
function resolvePageSize(raw: number): number {
  if (raw <= 0) return 0
  const sizes = PAGE_SIZES as readonly number[]
  return sizes.includes(raw) ? raw : PAGE_SIZES[0]
}

/**
 * 文件列表数据源。
 *
 * 与旧前端 `usePath` 的差异：
 * - **不缓存历史**（旧版有 path→objs 的 LRU 历史与滚动恢复）。先做正确性，
 *   M1 后续再按需要加回；否则一次性搬过来会有大量隐式状态难以对齐。
 * - **不做 fsGet/fsList 的「已知是目录」推测**，每次进入都先 fsGet 判定类型：
 *   多一次请求，但不会出现「把目录当文件渲染」的错判。
 * - 路径是 URL query 的唯一真源（`/?path=/a/b`），刷新/前进后退都自然正确。
 */
export function useFileList() {
  const route = useRoute()
  const router = useRouter()
  const settings = useSettingsStore()

  const path = computed(() => standardizePath(String(route.query.path ?? "/")))
  const password = ref("")

  const state = ref<ViewState>("loading")
  const errorMessage = ref("")

  const objs = ref<Obj[]>([])
  const total = ref(0)
  const provider = ref("")
  const canWrite = ref(false)
  const readme = ref("")
  const current = ref<FsGetData | null>(null)
  /** 当前目录可用的直传工具（如 HttpDirect），供上传面板挑选通道。 */
  const directUploadTools = ref<string[] | undefined>(undefined)

  const sortKey = ref<SortKey>("name")
  const sortOrder = ref<SortOrder>("asc")

  /** 目录内过滤词（文件名子串，大小写不敏感）。 */
  const keyword = ref("")
  /** 当前页（1 基）。pageSize<=0 时无意义。 */
  const page = ref(1)
  /**
   * 每页条数：跟随站点设置 `default_page_size`（Public 设置，默认 30）。
   * 0 = 不分页 —— 与旧前端的语义一致，也留了一条「大目录一次看全」的逃生路径。
   * 站点配了候选之外的数字时回落到 30：分页器的 sizes 下拉只列候选，
   * 传一个不在候选里的值会让 Element Plus 显示空白。
   */
  const pageSize = ref(resolvePageSize(settings.getNumber("default_page_size", 30)))

  /**
   * 分页方式（设置 `pagination_type`，`all` / `pagination`）。
   *
   * `all` 时一次渲染全部条目（`paginate` 收到 pageSize=0 会原样返回），
   * 分页条也会因为 `pages === 1` 自动隐藏 —— 不用再给宿主加一个开关。
   * 上游默认值就是 `all`：小目录里翻页纯粹是负担。
   */
  const paged = computed(() => settings.get("pagination_type") !== "all")

  /** 目录恒在前，再按选定字段排序；同名时退化为名字比较，保证顺序稳定。 */
  const sortedObjs = computed(() => {
    const factor = sortOrder.value === "asc" ? 1 : -1
    return [...objs.value].sort((a, b) => {
      if (a.is_dir !== b.is_dir) return a.is_dir ? -1 : 1
      let diff = 0
      if (sortKey.value === "name") diff = a.name.localeCompare(b.name, "zh-CN")
      else if (sortKey.value === "size") diff = a.size - b.size
      else diff = Date.parse(a.modified) - Date.parse(b.modified)
      if (diff === 0) diff = a.name.localeCompare(b.name, "zh-CN")
      return diff * factor
    })
  })

  /** 过滤后的全集（分页的输入，也是「共 N 项」里 N 的来源之一）。 */
  const filteredObjs = computed(() => {
    const matched = filterObjs(sortedObjs.value, keyword.value)
    if (kindFilter.value === "all") return matched
    return matched.filter((obj) => obj.is_dir || obj.type === ObjType.IMAGE)
  })

  /** 当前页要渲染的条目。排序/过滤/分页三者共用这一条流水线。 */
  const pagedObjs = computed(() =>
    paginate(filteredObjs.value, page.value, paged.value ? pageSize.value : 0),
  )

  /** 供 UI 直接用：当前页的可见条目。 */
  const viewObjs = computed(() => pagedObjs.value.items)

  /** 过滤是否生效（用于「无匹配结果」与「目录为空」两种空态的区分）。 */
  const filtering = computed(() => (keyword.value ?? "").trim().length > 0)

  /**
   * 内容类型过滤。图墙视图下只保留「目录 + 图片」，
   * 并且**参与分页**——否则 46 项 / 每页 30 会把 5 张图摊到两页里，
   * 分页器写着「共 46 项」，用户看到的却只有 5 个格子。
   */
  const kindFilter = ref<"all" | "image">("all")

  /** 真正的请求体：先 fsGet 判定类型，是目录再拉列表。 */
  async function load(force = false) {
    state.value = "loading"
    errorMessage.value = ""
    current.value = null

    const getResp = await fsGet(path.value, password.value)
    if (getResp.code === 403) {
      state.value = "need-password"
      return
    }
    if (getResp.code !== 200) {
      errorMessage.value = getResp.message
      state.value = "error"
      return
    }

    const detail = getResp.data
    if (!detail.is_dir) {
      current.value = detail
      state.value = "file"
      return
    }

    // per_page = 0 表示「不分页」，一次取全量；M1 后续接设置里的 default_page_size 时再改。
    const listResp = await fsList(path.value, password.value, 1, 0, force)
    if (listResp.code === 403) {
      state.value = "need-password"
      return
    }
    if (listResp.code !== 200) {
      errorMessage.value = listResp.message
      state.value = "error"
      return
    }

    objs.value = listResp.data.content ?? []
    total.value = listResp.data.total ?? objs.value.length
    provider.value = listResp.data.provider
    canWrite.value = listResp.data.write
    // 后端在「没有任何 meta readme」时下发的是 null（不是空串），这里归一，
    // 免得下游每个消费点都要写 `?? ""`。
    readme.value = listResp.data.readme ?? ""
    directUploadTools.value = listResp.data.direct_upload_tools
    page.value = 1
    state.value = "folder"
  }

  /** 进入新路径（写入 URL，由 watch 触发加载，保证「状态只跟 URL 走」）。 */
  function go(target: string, replace = false) {
    const next = standardizePath(target)
    const query = { ...route.query, path: next === "/" ? undefined : next }
    return replace ? router.replace({ path: route.path, query }) : router.push({ path: route.path, query })
  }

  function goUp() {
    const parent = path.value.split("/").slice(0, -1).join("/") || "/"
    return go(parent)
  }

  function toggleSort(key: SortKey) {
    if (sortKey.value === key) {
      sortOrder.value = sortOrder.value === "asc" ? "desc" : "asc"
    } else {
      sortKey.value = key
      sortOrder.value = "asc"
    }
  }

  /**
   * 打开一个条目。
   *
   * 文件也走 URL（`?path=/a/b.txt`），不另走一条「原地切状态」的分支：
   * 这样刷新/复制链接/前进后退都能回到同一个视图，且只保留一套类型判定逻辑。
   */
  function open(obj: Obj) {
    return go(`${path.value === "/" ? "" : path.value}/${obj.name}`)
  }

  // 只依赖 path：密码提交/手动刷新走 load()，避免 watch 里再判断一次来源
  watch(path, () => {
    // 换目录时过滤词与页码必须重置：否则会带着上一个目录的关键字把新目录过滤成空列表。
    keyword.value = ""
    page.value = 1
    void load()
  }, { immediate: true })

  // 过滤词或每页条数变化时回到第 1 页；删除文件后总数变小导致页码越界也靠下面夹回来。
  watch([keyword, pageSize], () => (page.value = 1))
  // 切换图墙会改变可见集合，同样要回第 1 页（否则可能停在已不存在的第 3 页）。
  watch(kindFilter, () => (page.value = 1))
  watch(
    () => pagedObjs.value.page,
    (p) => {
      if (p !== page.value) page.value = p
    },
  )

  return {
    path,
    password,
    state,
    errorMessage,
    objs,
    sortedObjs,
    filteredObjs,
    pagedObjs,
    viewObjs,
    keyword,
    filtering,
    kindFilter,
    page,
    pageSize,
    paged,
    total,
    provider,
    canWrite,
    readme,
    current,
    directUploadTools,
    sortKey,
    sortOrder,
    load,
    /** 手动刷新：走 force，绕过后端目录缓存。 */
    refresh: () => load(true),
    go,
    goUp,
    open,
    toggleSort,
  }
}

/** 把 ObjType 映射成图标语义，供列表与网格共用。 */
export const objKind = (obj: Obj): "folder" | "video" | "audio" | "image" | "text" | "other" => {
  if (obj.is_dir) return "folder"
  switch (obj.type) {
    case ObjType.VIDEO:
      return "video"
    case ObjType.AUDIO:
      return "audio"
    case ObjType.IMAGE:
      return "image"
    case ObjType.TEXT:
      return "text"
    default:
      return "other"
  }
}
