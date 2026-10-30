import { beforeAll, describe, expect, it } from "vitest"
import { mount } from "@vue/test-utils"
import { createApp, defineComponent, h, nextTick } from "vue"
import { VxeTableMenuModule } from "vxe-table"
import { VexTable, VexColumn } from "~/components/table"

/**
 * 适配层语义测试。
 *
 * 这里守的是三条「ElTable 有、vxe 没有」的行为：
 * 1. `selection-change` 在编程式改选后也会触发（vxe 的 setCheckboxRow 是静默的）；
 * 2. `row-contextmenu` 能带出 row 与 MouseEvent（依赖 vxe 菜单模块，同 main.tsx）；
 * 3. `toggleAllSelection` / `clearSelection` / `toggleRowSelection` 三个方法可用。
 */
const rows = [
  { id: 1, name: "alpha" },
  { id: 2, name: "beta" },
]

beforeAll(() => {
  ;(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  }
  // 与 main.tsx 保持一致：不装菜单模块，vxe 不会派发 cell-menu
  VxeTableMenuModule.install(createApp({ render: () => null }) as never)
})

/** 等 vxe 内部 nextTick 链跑完（选择状态要经过两次微任务才落定）。 */
async function settle() {
  await nextTick()
  await new Promise((resolve) => setTimeout(resolve, 0))
  await nextTick()
}

interface HostHandlers {
  onSelectionChange?: (rows: unknown[]) => void
  onRowContextmenu?: (row: unknown, column: unknown, event: Event) => void
}

function mountHost(handlers: HostHandlers = {}) {
  let table: Record<string, (...args: never[]) => unknown> | null = null
  const Host = defineComponent({
    setup() {
      return () =>
        h(
          VexTable,
          { ref: (el: unknown) => (table = el as never), data: rows, rowKey: "id", ...handlers } as never,
          {
            default: () => [
              h(VexColumn, { type: "selection", width: 42 } as never),
              h(VexColumn, { label: "名称", prop: "name" } as never),
            ],
          },
        )
    },
  })
  const wrapper = mount(Host, { attachTo: document.body })
  return { wrapper, table: table as never as Record<string, (...a: never[]) => unknown> | null }
}

describe("VexTable 适配层", () => {
  it("按 ElTable 的属性渲染表头与单元格", async () => {
    const { wrapper } = mountHost()
    await settle()
    const html = wrapper.html()
    expect(html).toContain("名称")
    expect(html).toContain("alpha")
    expect(html).toContain("beta")
    wrapper.unmount()
  })

  it("右键带出 row 与 MouseEvent", async () => {
    const got: unknown[][] = []
    const { wrapper } = mountHost({ onRowContextmenu: (...args: unknown[]) => got.push(args) })
    await settle()

    // 第一行最后一列（数据列），确认取到的是该行的 row
    const target = wrapper.element.querySelector("tbody tr:first-child td:last-child") as HTMLElement
    target.dispatchEvent(new MouseEvent("contextmenu", { bubbles: true, clientX: 10, clientY: 20 }))
    await settle()

    expect(got.length).toBe(1)
    expect((got[0][0] as { name: string }).name).toBe("alpha")
    expect(got[0][2]).toBeInstanceOf(Event)
    wrapper.unmount()
  })

  it("toggleRowSelection / toggleAllSelection / clearSelection 都会发出 selection-change", async () => {
    const events: unknown[][] = []
    const { wrapper, table } = mountHost({ onSelectionChange: (r: unknown[]) => events.push(r) })
    await settle()

    table?.toggleRowSelection?.(rows[0] as never, true as never)
    await settle()
    expect(events.at(-1)?.length).toBe(1)

    table?.toggleAllSelection?.()
    await settle()
    expect(events.at(-1)?.length).toBe(2)

    table?.clearSelection?.()
    await settle()
    expect(events.at(-1)?.length).toBe(0)
    wrapper.unmount()
  })
})
