import SwiftUI

struct UpdateRecoveryNotice: View {
    @ObservedObject private var recovery = Shared.updateRecovery

    var body: some View {
        ForEach(recovery.notices) { notice in
            VStack(alignment: .leading, spacing: 8) {
                Label(notice.message, systemImage: "arrow.clockwise")
                    .fixedSize(horizontal: false, vertical: true)
                    .identified("recovery.message")
                if !notice.extras.isEmpty {
                    Text("An extra default Claude also opened during this restart.")
                        .foregroundStyle(.secondary)
                    Button("Close Extra Claude…") { recovery.closeExtras(notice) }
                        .identified("recovery.close-extra")
                }
                HStack {
                    if notice.needsDecision {
                        Button("Reopen Anyway") { recovery.reopen(notice) }
                            .identified("recovery.reopen")
                    }
                    Button(L10n.text(notice.needsDecision ? "Leave Closed" : "Dismiss")) { recovery.dismiss(notice) }
                        .identified("recovery.dismiss")
                }
            }
            .font(.callout)
            .padding(12)
            .controls(profile: notice.request.profile)
            Divider()
        }
    }
}
