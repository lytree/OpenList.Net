import { computed, defineComponent, ref, watch } from "vue"
import {
  ElAlert,
  ElButton,
  ElDialog,
  ElInput,
  ElMessage,
  ElOption,
  ElSelect,
} from "element-plus"
import { DeletePolicy, fetchOfflineDownloadTools, fsAddOfflineDownload } from "~/api/fs"

/**
 * 离线下载（服务端代下）。
 *
 * 后端的可用工具来自 `GET /api/public/offline_download_tools`（由站点设置里启用的
 * aria2 / qbit / transmission 等派生）。**一个都没启用时**这个端点返回空数组，
 * 此时后端 `/fs/add_offline_download` 会直接 501 —— 所以先在前端拦住并给出可行动提示，
 * 而不是让用户填完 URL 才失败。
 *
 * `delete_policy` 是 int 枚举（对齐 Go `model.DeletePolicy`），值为字符串会被绑定成 0。
 * 目前后端只透传给下载器、尚未实现删除动作，UI 仍然展示以对齐上游行为。
 */
const POLICIES: { value: DeletePolicy; label: string }[] = [
  { value: DeletePolicy.DeleteOnUploadSucceed, label: "上传成功后删除" },
  { value: DeletePolicy.DeleteOnUploadFailed, label: "上传失败后删除" },
  { value: DeletePolicy.DeleteNever, label: "从不删除" },
  { value: DeletePolicy.DeleteAlways, label: "总是删除" },
  { value: DeletePolicy.UploadDownloadStream, label: "边下边传（仅 SimpleHttp）" },
]

export default defineComponent({
  name: "OfflineDownloadDialog",
  props: {
    visible: { type: Boolean, required: true },
    dir: { type: String, required: true },
    /**
     * 打开时预填的链接。种子预览里的「离线下载」会把磁力链接塞进来；
     * 每次打开都按它重置，避免上一次残留的文本混进来。
     */
    initialUrls: { type: Array as () => string[], default: () => [] },
  },
  emits: {
    "update:visible": (_v: boolean) => true,
    done: () => true,
  },
  setup(props, { emit }) {
    const tools = ref<string[]>([])
    const tool = ref("")
    const policy = ref<DeletePolicy>(DeletePolicy.DeleteOnUploadSucceed)
    const urlsText = ref("")
    const busy = ref(false)
    const loading = ref(false)

    const urls = computed(() =>
      urlsText.value
        .split("\n")
        .map((line) => line.trim())
        .filter(Boolean),
    )

    async function loadTools() {
      loading.value = true
      try {
        const resp = await fetchOfflineDownloadTools()
        tools.value = resp.code === 200 ? (resp.data ?? []) : []
        tool.value = tools.value[0] ?? ""
        // 只有 SimpleHttp 支持「边下边传」，切到别的工具时把策略换成安全默认值。
        if (tool.value !== "SimpleHttp") policy.value = DeletePolicy.DeleteOnUploadSucceed
      } finally {
        loading.value = false
      }
    }

    watch(
      () => props.visible,
      (open) => {
        if (!open) return
        urlsText.value = props.initialUrls.join("\n")
        policy.value = DeletePolicy.DeleteOnUploadSucceed
        void loadTools()
      },
    )

    function close() {
      emit("update:visible", false)
    }

    async function submit() {
      if (!tool.value) {
        ElMessage.warning("站点未启用任何离线下载工具")
        return
      }
      if (urls.value.length === 0) {
        ElMessage.warning("请至少填写一个链接")
        return
      }
      busy.value = true
      try {
        const resp = await fsAddOfflineDownload(props.dir, urls.value, tool.value, policy.value)
        if (resp.code !== 200) {
          ElMessage.error(resp.message || "提交失败")
          return
        }
        ElMessage.success("已提交离线下载任务")
        emit("done")
        close()
      } finally {
        busy.value = false
      }
    }

    return () => (
      <ElDialog
        modelValue={props.visible}
        title="离线下载"
        width="560px"
        onUpdate:modelValue={(v: boolean) => emit("update:visible", v)}
        v-slots={{
          default: () => (
            <div class="ol-dialog-col">
              {!loading.value && tools.value.length === 0 && (
                <ElAlert
                  type="warning"
                  showIcon={true}
                  closable={false}
                  title="当前站点没有启用任何离线下载工具（aria2 / qBittorrent / Transmission），请先在管理台设置里启用。"
                />
              )}
              <div class="ol-dialog-field">
                <span class="ol-dialog-label">下载工具</span>
                <ElSelect
                  modelValue={tool.value}
                  size="small"
                  disabled={tools.value.length === 0}
                  style={{ width: "220px" }}
                  onUpdate:modelValue={(v: string) => {
                    tool.value = v
                    if (v !== "SimpleHttp") policy.value = DeletePolicy.DeleteOnUploadSucceed
                  }}
                >
                  {tools.value.map((name) => (
                    <ElOption key={name} label={name} value={name} />
                  ))}
                </ElSelect>
              </div>
              <div class="ol-dialog-field">
                <span class="ol-dialog-label">链接（每行一个，支持 http / 磁力链接）</span>
                <ElInput
                  modelValue={urlsText.value}
                  type="textarea"
                  rows={6}
                  placeholder={"https://example.com/file.iso\nmagnet:?xt=urn:btih:..."}
                  onUpdate:modelValue={(v: string) => (urlsText.value = v)}
                />
              </div>
              <div class="ol-dialog-field">
                <span class="ol-dialog-label">下载完成后</span>
                <ElSelect
                  modelValue={policy.value}
                  size="small"
                  style={{ width: "260px" }}
                  onUpdate:modelValue={(v: number) => (policy.value = Number(v) as DeletePolicy)}
                >
                  {POLICIES.filter((p) => p.value !== DeletePolicy.UploadDownloadStream || tool.value === "SimpleHttp").map(
                    (p) => (
                      <ElOption key={p.value} label={p.label} value={p.value} />
                    ),
                  )}
                </ElSelect>
              </div>
              <span class="ol-dialog-hint">
                保存目录：{props.dir}。任务在后台执行，可到管理台「任务」查看进度。
              </span>
            </div>
          ),
          footer: () => (
            <div class="ol-dialog-footer">
              <ElButton onClick={close}>取消</ElButton>
              <ElButton
                type="primary"
                loading={busy.value}
                disabled={tools.value.length === 0}
                onClick={submit}
              >
                提交任务
              </ElButton>
            </div>
          ),
        }}
      />
    )
  },
})
