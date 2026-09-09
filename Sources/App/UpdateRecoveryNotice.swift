import SwiftUI

struct UpdateRecoveryNotice: View {
    @ObservedObject private var recovery = Shared.updateRecovery

    var body: some View {
        ForEach(recovery.notices) { notice in
            VStack(alignment: .leading, spacing: 8) {
                Label(notice.message, systemImage: "arrow.clockwise")
                    .fixedSize(horizontal: false, vertical: true)
                if !notice.extras.isEmpty {
                    Text("An extra default Claude also opened during this restart.")
                        .foregroundStyle(.secondary)
                    Button("Close Extra Claude…") { recovery.closeExtras(notice) }
                }
                HStack {
                    if notice.needsDecision {
                        Button("Reopen Anyway") { recovery.reopen(notice) }
                    }
                    Button(L10n.text(notice.needsDecision ? "Leave Closed" : "Dismiss")) { recovery.dismiss(notice) }
                }
            }
            .font(.callout)
            .padding(12)
            Divider()
        }
    }
}
