import { defineComponent, computed, ref, watch } from "vue"
import {
  ElBreadcrumb,
  ElBreadcrumbItem,
  ElButton,
  ElDialog,
  ElEmpty,
  ElIcon,
  ElMessage,
  ElSpace,
} from "element-plus"
import { Folder, FolderOpened } from "@element-plus/icons-vue"
import { fsDirs } from "~/api/fs"
import { pathSegments } from "~/utils/path"
import { t } from "~/i18n"

/**
 * 目标目录选择器（移动/复制共用）。
 *
 * 后端 `fs/dirs` 一次返回初始目录下**深度 3 以内的全部子目录全路径**
 * （`DirsResp.Dirs`），所以这里不发逐层请求：一次加载，前端按当前目录
 * 过滤出直接子目录；面包屑点击向上回退。
 */
const FolderPickerDialog = defineComponent({
  name: "FolderPickerDialog",
  props: {
    visible: { type: Boolean, required: true },
    title: { type: String, required: true },
    /** 打开时的初始目录（也是可选范围的根）。 */
    initialDir: { type: String, required: true },
    /** 源目录：与所选目录相同时不允许确认（原地移动/复制无意义）。 */
    sourceDir: { type: String, default: "" },
  },
  emits: {
    "update:visible": (_v: boolean) => true,
    chosen: (_dir: string) => true,
  },
  setup(props, { emit }) {
    const dir = ref(props.initialDir)
    const allDirs = ref<string[]>([])
    const loading = ref(false)

    /**
     * 加载某个目录的子树（深度 3）。
     * 进入子目录不重新请求（子树已包含）；面包屑向上回退跨出已加载子树时才重新请求。
     */
    async function load(root: string) {
      loading.value = true
      try {
        const resp = await fsDirs(root)
        if (resp.code !== 200) {
          ElMessage.error(resp.message || "error")
          return
        }
        allDirs.value = resp.data?.dirs ?? []
        dir.value = root
      } finally {
        loading.value = false
      }
    }

    watch(
      () => props.visible,
      (open) => {
        if (open) void load(props.initialDir)
      },
    )

    /** 当前目录下的直接子目录名。 */
    const children = computed(() => {
      const prefix = dir.value === "/" ? "/" : `${dir.value}/`
      return allDirs.value
        .filter((p) => p.startsWith(prefix) && p !== dir.value)
        .map((p) => p.slice(prefix.length))
        .filter((name) => !name.includes("/"))
        .sort((a, b) => a.localeCompare(b, "zh-CN"))
    })

    const segments = () => pathSegments(dir.value, "/")

    function enter(child: string) {
      dir.value = dir.value === "/" ? `/${child}` : `${dir.value}/${child}`
    }

    function confirm() {
      if (dir.value === props.sourceDir) {
        ElMessage.warning("same directory")
        return
      }
      emit("chosen", dir.value)
      emit("update:visible", false)
    }

    return () => (
      <ElDialog
        modelValue={props.visible}
        title={props.title}
        width="420px"
        onUpdate:modelValue={(v: boolean) => emit("update:visible", v)}
      >
        <ElBreadcrumb separator="/" style={{ marginBottom: "12px" }}>
          {segments().map((seg, index) => (
            <ElBreadcrumbItem>
              <span
                class={index === segments().length - 1 ? "ol-crumb ol-crumb-current" : "ol-crumb"}
                onClick={() => {
                  if (index !== segments().length - 1) void load(seg.path)
                }}
              >
                {seg.name}
              </span>
            </ElBreadcrumbItem>
          ))}
        </ElBreadcrumb>

        <div class="ol-picker-list">
          {loading.value && (
            <div class="ol-files-center" style={{ padding: "16px 0" }}>
              <ElIcon class="is-loading">
                <FolderOpened />
              </ElIcon>
            </div>
          )}
          {!loading.value && children.value.length === 0 && (
            <ElEmpty description={t("ops.no_subfolder")} imageSize={64} />
          )}
          {!loading.value &&
            children.value.map((name) => (
              <div key={name} class="ol-picker-item" onClick={() => enter(name)}>
                <ElIcon size={16} color="#e6a23c">
                  <Folder />
                </ElIcon>
                <span>{name}</span>
              </div>
            ))}
        </div>

        <ElSpace style={{ marginTop: "12px", display: "flex", justifyContent: "flex-end" }}>
          <ElButton onClick={() => emit("update:visible", false)}>{t("global.cancel")}</ElButton>
          <ElButton type="primary" onClick={confirm}>
            {t("ops.choose_here")}
          </ElButton>
        </ElSpace>
      </ElDialog>
    )
  },
})

export default FolderPickerDialog
