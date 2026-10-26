import { ref, reactive, onMounted } from "vue"
import {
  ElButton,
  ElTable,
  ElTableColumn,
  ElForm,
  ElFormItem,
  ElInput,
  ElSelect,
  ElOption,
  ElDialog,
  ElDropdown,
  ElDropdownMenu,
  ElDropdownItem,
  ElLoading,
  ElTag,
  ElMessage,
  type FormInstance,
  type FormRules,
} from "element-plus"

// M0 互操作验证页：逐项覆盖 Element Plus 在 Vapor 下的高风险面。
// 1) 表格自定义插槽（作用域插槽） 2) 表单校验 + ref  3) 对话框（teleport+插槽）
// 4) 右键菜单（trigger=contextmenu） 5) ElLoading 服务式 API（替代 v-loading 指令）

// TSX 约定①：给 Element Plus 传插槽时只走 v-slots，不要再写 children（否则 v-slots 重复）。
// TSX 约定②：插槽参数不要手写窄类型，让组件类型推断（el-table 的 row 为 DefaultRow）。
type Row = {
  id: number
  name: string
  size: string
  status: "ok" | "warn"
}

const rows = ref<Row[]>([
  { id: 1, name: "本地存储 /", size: "1.2 TB", status: "ok" },
  { id: 2, name: "阿里云盘 /backup", size: "480 GB", status: "warn" },
  { id: 3, name: "S3 /media", size: "8.6 TB", status: "ok" },
])

const formRef = ref<FormInstance>()
const form = reactive({ name: "", storage: "local" })
const rules: FormRules = {
  name: [{ required: true, message: "请输入名称", trigger: "blur" }],
}

const dialogVisible = ref(false)
const loading = ref(false)

onMounted(() => {
  console.log("[spike] mounted, rows =", rows.value.length)
})

async function validate() {
  const ok = await formRef.value?.validate().catch(() => false)
  if (ok === true) ElMessage.success(`校验通过：${form.name} / ${form.storage}`)
}

function openLoading() {
  const inst = ElLoading.service({ lock: true, text: "加载中…", background: "rgba(0,0,0,.35)" })
  setTimeout(() => {
    inst.close()
    ElMessage.success("Loading 服务可用")
  }, 800)
}

function onCommand(cmd: string) {
  ElMessage.info(`右键菜单：${cmd}`)
}

export default function Spike() {
  return (
    <div style={{ padding: "24px", maxWidth: "880px", margin: "0 auto" }}>
      <h1 style={{ fontSize: "20px", margin: "0 0 4px" }}>Vapor + Element Plus 互操作验证</h1>
      <p style={{ color: "#666", fontSize: "13px", margin: "0 0 20px" }}>
        vue-jsx（VDOM）· Element Plus vdom 组件 · 纯 VDOM 基线
      </p>

      <ElForm ref={formRef} model={form} rules={rules} labelWidth="80px" style={{ marginBottom: "20px" }}>
        <ElFormItem label="名称" prop="name">
          <ElInput v-model={form.name} placeholder="输入名称后失焦触发校验" />
        </ElFormItem>
        <ElFormItem label="存储">
          <ElSelect v-model={form.storage} style={{ width: "200px" }}>
            <ElOption label="本地" value="local" />
            <ElOption label="S3" value="s3" />
          </ElSelect>
        </ElFormItem>
        <ElFormItem>
          <ElButton type="primary" onClick={validate}>
            校验
          </ElButton>
          <ElButton onClick={() => (dialogVisible.value = true)}>打开对话框</ElButton>
          <ElButton onClick={openLoading}>Loading 服务</ElButton>
        </ElFormItem>
      </ElForm>

      <ElDropdown trigger="contextmenu" onCommand={onCommand} v-slots={{
        default: () => (
          <div
            style={{
              padding: "8px 12px",
              border: "1px dashed #c0c4cc",
              borderRadius: "6px",
              fontSize: "13px",
              color: "#606266",
              marginBottom: "16px",
            }}
          >
            在此区域内点击右键 → 验证 contextmenu 触发器
          </div>
        ),
        dropdown: () => (
          <ElDropdownMenu>
            <ElDropdownItem command="rename">重命名</ElDropdownItem>
            <ElDropdownItem command="delete" divided>
              删除
            </ElDropdownItem>
          </ElDropdownMenu>
        ),
      }} />

      <ElTable data={rows.value} border style={{ width: "100%" }}>
        <ElTableColumn prop="id" label="#" width="60" />
        <ElTableColumn prop="name" label="名称" v-slots={{
          default: ({ row }) => <span style={{ fontWeight: 500 }}>{row.name}</span>,
        }} />
        <ElTableColumn prop="size" label="占用" width="120" />
        <ElTableColumn label="状态" width="100" v-slots={{
          default: ({ row }) => (
            <ElTag type={row.status === "ok" ? "success" : "warning"} size="small">
              {row.status === "ok" ? "正常" : "注意"}
            </ElTag>
          ),
        }} />
      </ElTable>

      <ElDialog v-model={dialogVisible.value} title="对话框验证" width="420px" v-slots={{
        default: () => (
          <p style={{ margin: 0, fontSize: "13px" }}>
            对话框内容插槽 + teleport 在 Vapor 树下的表现。
          </p>
        ),
        footer: () => (
          <div>
            <ElButton onClick={() => (dialogVisible.value = false)}>取消</ElButton>
            <ElButton type="primary" onClick={() => (dialogVisible.value = false)}>
              确定
            </ElButton>
          </div>
        ),
      }} />
    </div>
  )
}
