package com.rextechnologies.sightline.core.camera

import com.rextechnologies.sightline.core.link.CameraLinkException
import com.rextechnologies.sightline.core.link.LinkFailure
import com.rextechnologies.sightline.core.session.CameraTimeoutException
import com.rextechnologies.sightline.protocol.gpsock.GpSockProtocolException
import java.io.IOException
import java.net.ConnectException
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class ProblemTest {
    @Test
    fun `each way of failing to join reads as what fixes it`() {
        fun joining(failure: LinkFailure) = Problem.whileJoining(CameraLinkException(failure, "detail")).kind

        assertEquals(ProblemKind.NotJoined, joining(LinkFailure.Unavailable))
        assertEquals(ProblemKind.WifiOff, joining(LinkFailure.WifiOff))
        assertEquals(ProblemKind.PermissionDenied, joining(LinkFailure.PermissionDenied))
    }

    @Test
    fun `a camera that is silent or drops the socket did not answer, and one that hangs up was lost`() {
        assertEquals(ProblemKind.NoAnswer, Problem.whileTalking(CameraTimeoutException("slow")).kind)
        assertEquals(ProblemKind.NoAnswer, Problem.whileTalking(ConnectException("refused")).kind)
        assertEquals(ProblemKind.Lost, Problem.whileTalking(GpSockProtocolException("closed")).kind)
        assertEquals(ProblemKind.Unexpected, Problem.whileTalking(IllegalStateException("odd")).kind)
    }

    @Test
    fun `the detail keeps what happened and the explanation stays a sentence`() {
        val problem = Problem.whileTalking(IOException("Connection reset"))

        assertEquals("Connection reset", problem.detail)
        assertEquals(ProblemKind.NoAnswer.explanation, problem.explanation)
        assertEquals("IOException", Problem.whileTalking(IOException()).detail)
        assertEquals("", Problem.whileJoining(CameraLinkException(LinkFailure.WifiOff, "")).detail)
    }

    @Test
    fun `every explanation is a sentence with a remedy`() {
        for (kind in ProblemKind.entries) {
            assertTrue(kind.explanation.endsWith("."), kind.name)
        }

        assertEquals(Remedy.OpenWifiSettings, ProblemKind.WifiOff.remedy)
        assertEquals(Remedy.OpenAppPermissions, ProblemKind.PermissionDenied.remedy)
        assertEquals(setOf(Remedy.TryAgain), ProblemKind.entries.drop(2).map { it.remedy }.toSet())
    }
}
