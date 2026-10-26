import { computed, defineComponent, ref, watch, type PropType } from "vue"
import {
  ElAlert,
  ElButton,
  ElDatePicker,
  ElDialog,
  ElInput,
  ElInputNumber,
  ElMessage,
} from "element-plus"
import { createShare } from "~/api/shares"
import { useUserStore } from "~/stores/user"
import { pathJoin } from "~/utils/path"
import type { Obj } from "~/api/types"

/**
 * 创建分享（首页快捷入口）。
 *
 * 与管理台 `Shares.tsx` 的差别：这里**只创建，不编辑**，并且文件列表来自当前选中项，
 * 不允许在弹窗里改路径 —— 首页的意图是「把刚选中的东西分享出去」，改路径应该去管理台。
 *
 * 路径要带上用户的 `base_path`：分享存的是**站点绝对路径**，而浏览时用的是相对用户根
 * 的路径。漏掉 base_path 会让非管理员分享出指向不存在路径的码（上游同样处理）。
 */
function randomPwd(len = 8): string {
  const chars = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789"
  let out = ""
  const buf = new Uint32Array(len)
  crypto.getRandomValues(buf)
  for (let i = 0; i < len; i++) out += chars[buf[i] % chars.length]
  return out
}

export default defineComponent({
  name: "ShareDialog",
  props: {
    visible: { type: Boolean, required: true },
    dir: { type: String, required: true },
    targets: { type: Array as PropType<Obj[]>, default: () => [] },
  },
  emits: {
    "update:visible": (_v: boolean) => true,
  },
  setup(props, { emit }) {
    const user = useUserStore()

    const code = ref("")
    const pwd = ref("")
    const expires = ref<Date | null>(null)
    const maxAccessed = ref(0)
    const remark = ref("")
    const busy = ref(false)
    /** 创建成功后的分享链接；非空时弹窗切到「结果」视图。 */
    const createdLink = ref("")

    /** 分享路径（带 base_path），展示与提交共用同一份。 */
    const files = computed(() => {
      const base = (user.me?.base_path ?? "").replace(/\/+$/, "")
      return props.targets.map((obj) => `${base}${pathJoin(props.dir, obj.name)}`)
    })

    watch(
      () => props.visible,
      (open) => {
        if (!open) return
        code.value = ""
        pwd.value = ""
        expires.value = null
        maxAccessed.value = 0
        remark.value = ""
        createdLink.value = ""
      },
    )

    function close() {
      emit("update:visible", false)
    }

    async function submit() {
      if (files.value.length === 0) {
        ElMessage.warning("请先选择要分享的条目")
        return
      }
      busy.value = true
      try {
        const resp = await createShare({
          id: code.value.trim() || null,
          files: files.value,
          expires: expires.value ? expires.value.toISOString() : null,
          pwd: pwd.value || null,
          max_accessed: maxAccessed.value,
          disabled: false,
          // 服务端 UpdateAsync 会无条件写回 accessed；新建传 0 不影响语义，
          // 但字段是必填（后端 int 非可空）。
          accessed: 0,
          remark: remark.value || null,
        })
        if (resp.code !== 200) {
          ElMessage.error(resp.message || "创建分享失败")
          return
        }
        const shareCode = resp.data?.id ?? ""
        const base = location.href.split("#")[0]
        createdLink.value = `${base}#/s/${shareCode}`
        ElMessage.success("分享已创建")
      } finally {
        busy.value = false
      }
    }

    async function copyLink() {
      try {
        await navigator.clipboard.writeText(createdLink.value)
        ElMessage.success("已复制链接")
      } catch {
        ElMessage.error("复制失败，请手动选择文本")
      }
    }

    return () => (
      <ElDialog
        modelValue={props.visible}
        title={createdLink.value ? "分享已创建" : "创建分享"}
        width="540px"
        onUpdate:modelValue={(v: boolean) => emit("update:visible", v)}
        v-slots={{
          default: () =>
            createdLink.value ? (
              <div class="ol-dialog-col">
                <ElAlert
                  type="success"
                  showIcon={true}
                  closable={false}
                  title="链接可直接发给他人；带密码的分享需要对方输入密码。"
                />
                <ElInput modelValue={createdLink.value} readonly={true} />
                <span class="ol-dialog-hint">
                  公开分享页只读：访客能浏览目录与下载，不能上传或修改。
                </span>
              </div>
            ) : (
              <div class="ol-dialog-col">
                <div class="ol-dialog-field">
                  <span class="ol-dialog-label">分享内容（{files.value.length} 项）</span>
                  <div class="ol-dialog-list">
                    {files.value.map((path) => (
                      <span key={path} class="ol-dialog-path">
                        {path}
                      </span>
                    ))}
                  </div>
                </div>
                <div class="ol-dialog-field">
                  <span class="ol-dialog-label">分享码（留空自动生成）</span>
                  <ElInput
                    modelValue={code.value}
                    placeholder="4–16 位字母数字"
                    style={{ maxWidth: "240px" }}
                    onUpdate:modelValue={(v: string) => (code.value = v)}
                  />
                </div>
                <div class="ol-dialog-field">
                  <span class="ol-dialog-label">访问密码（可选）</span>
                  <div class="ol-dialog-row">
                    <ElInput
                      modelValue={pwd.value}
                      placeholder="留空表示无密码"
                      style={{ maxWidth: "240px" }}
                      onUpdate:modelValue={(v: string) => (pwd.value = v)}
                    />
                    <ElButton size="small" onClick={() => (pwd.value = randomPwd())}>
                      随机
                    </ElButton>
                  </div>
                </div>
                <div class="ol-dialog-field">
                  <span class="ol-dialog-label">过期时间（可选）</span>
                  <ElDatePicker
                    modelValue={expires.value}
                    type="datetime"
                    placeholder="永不过期"
                    style={{ maxWidth: "240px" }}
                    onUpdate:modelValue={(v: Date | null) => (expires.value = v)}
                  />
                </div>
                <div class="ol-dialog-field">
                  <span class="ol-dialog-label">最大访问次数（0 = 不限）</span>
                  <ElInputNumber
                    modelValue={maxAccessed.value}
                    min={0}
                    style={{ width: "160px" }}
                    onUpdate:modelValue={(v: number | undefined) => (maxAccessed.value = v ?? 0)}
                  />
                </div>
                <div class="ol-dialog-field">
                  <span class="ol-dialog-label">备注（仅自己可见）</span>
                  <ElInput
                    modelValue={remark.value}
                    placeholder="可选"
                    onUpdate:modelValue={(v: string) => (remark.value = v)}
                  />
                </div>
              </div>
            ),
          footer: () => (
            <div class="ol-dialog-footer">
              {createdLink.value ? (
                <>
                  <ElButton onClick={close}>关闭</ElButton>
                  <ElButton type="primary" onClick={copyLink}>
                    复制链接
                  </ElButton>
                </>
              ) : (
                <>
                  <ElButton onClick={close}>取消</ElButton>
                  <ElButton type="primary" loading={busy.value} onClick={submit}>
                    创建
                  </ElButton>
                </>
              )}
            </div>
          ),
        }}
      />
    )
  },
})
