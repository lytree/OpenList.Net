import { computed, defineComponent, reactive, ref } from "vue"
import {
  ElButton,
  ElCheckbox,
  ElDialog,
  ElIcon,
  ElMessage,
  ElOption,
  ElProgress,
  ElSelect,
  ElSpace,
  ElTag,
  ElText,
} from "element-plus"
import { Document, FolderAdd, RefreshRight } from "@element-plus/icons-vue"
import { t } from "~/i18n"
import { humanSize } from "~/utils/format"
import { pathJoin } from "~/utils/path"
import { asyncPool, File2Upload, traverseFileTree } from "./util"
import { useUploaders } from "./uploads"
import { StatusBadge, type SetUpload, type UploadFileProps, type UploadStatus } from "./types"

/**
 * 上传面板（对应旧前端 Upload.tsx）。
 *
 * 交互与旧版一致：拖拽区 + 选文件/选目录两个入口 + 上传通道选择 +
 * 覆盖/秒传选项；上传中切换为进度列表（状态徽标、速度、失败重试）。
 * as-task 选项暂缺：.NET 后端没有离线任务系统，传了也是 no-op。
 */

const STATUS_LABEL: Record<UploadStatus, string> = {
  pending: "home.upload.pending",
  hashing: "home.upload.hashing",
  uploading: "home.upload.uploading",
  backending: "home.upload.backending",
  success: "home.upload.success",
  error: "home.upload.error",
}

const UPLOAD_CONFIG_KEY = "upload_config"

function loadUploadConfig() {
  try {
    const raw = localStorage.getItem(UPLOAD_CONFIG_KEY)
    if (raw) return JSON.parse(raw) as { overwrite: boolean; rapid: boolean }
  } catch { /* 忽略坏数据 */ }
  return { overwrite: false, rapid: false }
}

export default defineComponent({
  name: "UploadPanel",
  props: {
    visible: { type: Boolean, required: true },
    /** 当前目录（上传目标）。 */
    dir: { type: String, required: true },
    /** 目录密码（加密目录需要）。 */
    password: { type: String, default: "" },
    /** 当前目录可用的直传工具（fs/list 的 direct_upload_tools）。 */
    directUploadTools: { type: Array as () => string[] | undefined, default: undefined },
  },
  emits: {
    "update:visible": (v: boolean) => typeof v === "boolean",
    /** 任一文件成功后触发（父组件据此刷新列表）。 */
    uploaded: () => true,
  },
  setup(props, { emit }) {
    const uploaders = useUploaders(() => props.directUploadTools)
    const availableUploaders = computed(() => uploaders.filter((u) => u.available()))
    const curUploaderName = ref(availableUploaders.value[0]?.name ?? "Stream")

    const config = reactive(loadUploadConfig())
    const saveConfig = () => {
      try { localStorage.setItem(UPLOAD_CONFIG_KEY, JSON.stringify({ ...config })) } catch { /* 忽略 */ }
    }

    const uploads = ref<UploadFileProps[]>([])
    const started = ref(false)
    const drag = ref(false)

    // File 句柄存着，失败行可以原地重试
    const fileMap = new Map<string, File>()
    const fileInput = ref<HTMLInputElement>()
    const folderInput = ref<HTMLInputElement>()

    const allDone = computed(() =>
      uploads.value.length > 0 &&
      uploads.value.every((u) => u.status === "success" || u.status === "error"),
    )

    const setUpload = (path: string, key: keyof UploadFileProps, value: unknown) => {
      const row = uploads.value.find((u) => u.path === path)
      if (row) (row as Record<string, unknown>)[key] = value
    }

    const runOne = async (file: File) => {
      const rel = file.name.replace(/\\/g, "/")
      setUpload(rel, "status", "uploading")
      setUpload(rel, "msg", "")
      const uploadPath = pathJoin(props.dir, rel)
      const uploader =
        availableUploaders.value.find((u) => u.name === curUploaderName.value) ??
        availableUploaders.value[0] ??
        uploaders[uploaders.length - 1]
      try {
        const set: SetUpload = (key, value) => setUpload(rel, key, value)
        const err = await uploader.upload(uploadPath, file, set, {
          password: props.password,
          overwrite: config.overwrite,
          rapid: config.rapid,
        }).catch((e: Error) => e)
        if (!err) {
          setUpload(rel, "status", "success")
          setUpload(rel, "progress", 100)
          emit("uploaded")
        } else {
          setUpload(rel, "status", "error")
          setUpload(rel, "msg", err.message)
        }
      } catch (e) {
        setUpload(rel, "status", "error")
        setUpload(rel, "msg", String((e as Error)?.message ?? e))
      }
    }

    const handleAddFiles = async (files: File[]) => {
      if (files.length === 0) return
      for (const file of files) {
        const upload = File2Upload(file)
        if (uploads.value.some((u) => u.path === upload.path)) continue
        fileMap.set(upload.path, file)
        uploads.value.push(upload)
      }
      started.value = true
      // 同时最多 3 个文件在上传
      for await (const _ of asyncPool(3, files, runOne)) {
        /* 全部跑完为止 */
      }
    }

    const onPickFiles = (e: Event) => {
      const input = e.target as HTMLInputElement
      handleAddFiles(Array.from(input.files ?? []))
      input.value = ""
    }

    const retryFile = (path: string) => {
      const file = fileMap.get(path)
      if (!file) return
      setUpload(path, "msg", "")
      setUpload(path, "progress", 0)
      setUpload(path, "speed", 0)
      void runOne(file)
    }

    const clearDone = () => {
      uploads.value = uploads.value.filter(
        (u) => u.status !== "success" && u.status !== "error",
      )
    }

    const onDrop = async (e: DragEvent) => {
      e.preventDefault()
      e.stopPropagation()
      drag.value = false
      const res: File[] = []
      const items = Array.from(e.dataTransfer?.items ?? [])
      const files = Array.from(e.dataTransfer?.files ?? [])
      const folderEntries: FileSystemEntry[] = []
      for (let i = 0; i < items.length; i++) {
        const entry = items[i].webkitGetAsEntry()
        if (entry?.isFile) {
          res.push(files[i])
        } else if (entry?.isDirectory) {
          folderEntries.push(entry)
        }
      }
      for (const entry of folderEntries) {
        res.push(...(await traverseFileTree(entry)))
      }
      if (res.length === 0) {
        ElMessage.warning(t("home.upload.no_files_drag"))
        return
      }
      void handleAddFiles(res)
    }

    const close = () => emit("update:visible", false)

    return () => (
      <ElDialog
        modelValue={props.visible}
        title={t("home.upload.upload_files")}
        width="560px"
        closeOnClickModal={false}
        onUpdate:modelValue={(v: boolean) => emit("update:visible", v)}
      >
        {!started.value ? (
          <div
            class="ol-upload-drop"
            style={{
              borderColor: drag.value ? "var(--el-color-primary)" : undefined,
            }}
            onDragover={(e: DragEvent) => {
              e.preventDefault()
              drag.value = true
            }}
            onDragleave={() => (drag.value = false)}
            onDrop={(e: DragEvent) => void onDrop(e)}
          >
            {drag.value ? (
              <p class="ol-upload-hint">{t("home.upload.release")}</p>
            ) : (
              <>
                <p class="ol-upload-hint">{t("home.upload.upload-tips")}</p>
                <ElSpace size={16} wrap={true}>
                  <ElButton
                    size="large"
                    onClick={() => fileInput.value?.click()}
                  >
                    <ElIcon style={{ marginRight: "6px" }}><Document /></ElIcon>
                    {t("home.upload.upload_files")}
                  </ElButton>
                  <ElButton
                    size="large"
                    type="primary"
                    plain={true}
                    onClick={() => folderInput.value?.click()}
                  >
                    <ElIcon style={{ marginRight: "6px" }}><FolderAdd /></ElIcon>
                    {t("home.upload.upload_folder")}
                  </ElButton>
                </ElSpace>
                <div class="ol-upload-options">
                  <ElSelect
                    modelValue={curUploaderName.value}
                    size="small"
                    style={{ width: "180px" }}
                    onUpdate:modelValue={(v: string) => (curUploaderName.value = v)}
                  >
                    {availableUploaders.value.map((u) => (
                      <ElOption key={u.name} label={u.name} value={u.name} />
                    ))}
                  </ElSelect>
                  <ElCheckbox
                    modelValue={config.overwrite}
                    onUpdate:modelValue={(v) => { config.overwrite = v === true; saveConfig() }}
                  >
                    {t("home.conflict_policy.overwrite_existing")}
                  </ElCheckbox>
                  <ElCheckbox
                    modelValue={config.rapid}
                    onUpdate:modelValue={(v) => { config.rapid = v === true; saveConfig() }}
                  >
                    {t("home.upload.try_rapid")}
                  </ElCheckbox>
                </div>
              </>
            )}
          </div>
        ) : (
          <div class="ol-upload-list">
            <ElSpace size={8} style={{ marginBottom: "8px" }}>
              <ElButton size="small" disabled={!allDone.value} onClick={close}>
                {t("home.upload.back")}
              </ElButton>
              <ElButton
                size="small"
                disabled={!uploads.value.some((u) => u.status === "success" || u.status === "error")}
                onClick={clearDone}
              >
                {t("home.upload.clear_done")}
              </ElButton>
            </ElSpace>
            {uploads.value.map((u) => (
              <div key={u.path} class="ol-upload-row">
                <div class="ol-upload-row-head">
                  <ElText class="ol-upload-name" truncated={true}>{u.path}</ElText>
                  <ElSpace size={8}>
                    <ElTag size="small" type={StatusBadge[u.status] as "info" | "warning" | "primary" | "success" | "danger"}>
                      {t(STATUS_LABEL[u.status])}
                    </ElTag>
                    {u.status === "uploading" && (
                      <span class="ol-upload-speed">{humanSize(u.speed)}/s</span>
                    )}
                    {u.status === "error" && (
                      <ElButton text={true} size="small" type="primary" onClick={() => retryFile(u.path)}>
                        <ElIcon style={{ marginRight: "4px" }}><RefreshRight /></ElIcon>
                        {t("home.upload.retry")}
                      </ElButton>
                    )}
                    <span class="ol-upload-size">{humanSize(u.size)}</span>
                  </ElSpace>
                </div>
                <ElProgress
                  percentage={Math.min(100, Math.round(u.progress))}
                  status={u.status === "error" ? "exception" : u.status === "success" ? "success" : undefined}
                  strokeWidth={6}
                  showText={false}
                />
                {u.msg && <div class="ol-upload-err">{u.msg}</div>}
              </div>
            ))}
          </div>
        )}

        {/* 隐藏的文件选择器 */}
        <input
          ref={fileInput}
          type="file"
          multiple={true}
          style={{ display: "none" }}
          onChange={onPickFiles}
        />
        <input
          ref={folderInput}
          type="file"
          multiple={true}
          // @ts-expect-error 目录选择专有属性
          webkitdirectory={true}
          style={{ display: "none" }}
          onChange={onPickFiles}
        />

      </ElDialog>
    )
  },
})
