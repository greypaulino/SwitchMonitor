package com.greypaulino.switchmonitor.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.gestures.animateScrollBy
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.greypaulino.switchmonitor.ui.theme.Card
import com.greypaulino.switchmonitor.ui.theme.Line
import com.greypaulino.switchmonitor.ui.theme.Raised
import com.greypaulino.switchmonitor.ui.theme.Text
import kotlinx.coroutines.launch

/** A rounded card like the desktop app's. */
@Composable
fun Panel(modifier: Modifier = Modifier, padding: PaddingValues = PaddingValues(16.dp), content: @Composable ColumnScope.() -> Unit) {
    Column(
        modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(22.dp))
            .background(Card)
            .border(1.dp, Line, RoundedCornerShape(22.dp))
            .padding(padding),
        content = content,
    )
}

@Composable
fun PanelTitle(text: String, modifier: Modifier = Modifier) {
    Text(text, modifier, fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Text)
}

/**
 * A row that scrolls sideways without a scroll bar: when more items wait on the right, the edge fades into the card
 * and a ">" button moves the row along. The fade and button go away at the end.
 */
@Composable
fun FadingRow(spacing: Dp, selected: Int = -1, content: LazyListScope.() -> Unit) {
    val state = rememberLazyListState()
    val scope = rememberCoroutineScope()
    // Keep the chosen item in sight (not under the fade) when it changes.
    LaunchedEffect(selected) {
        if (selected < 0) return@LaunchedEffect
        val info = state.layoutInfo
        val item = info.visibleItemsInfo.firstOrNull { it.index == selected }
        val fade = 90
        if (item == null || item.offset < 0 || item.offset + item.size > info.viewportEndOffset - fade) state.animateScrollToItem(selected)
    }
    Box(Modifier.fillMaxWidth()) {
        LazyRow(
            state = state,
            horizontalArrangement = Arrangement.spacedBy(spacing),
            contentPadding = PaddingValues(end = if (state.canScrollForward) 28.dp else 0.dp),
            modifier = Modifier.fillMaxWidth(),
            content = content,
        )
        if (state.canScrollForward) {
            Box(
                Modifier
                    .matchParentSize()
                    .padding(start = 0.dp),
                contentAlignment = Alignment.CenterEnd,
            ) {
                Box(
                    Modifier
                        .width(44.dp)
                        .fillMaxHeight()
                        .background(Brush.horizontalGradient(listOf(Card.copy(alpha = 0f), Card.copy(alpha = 0.75f)))),
                    contentAlignment = Alignment.CenterEnd,
                ) {
                    IconButton(
                        onClick = { scope.launch { state.animateScrollBy(220f * 3) } },
                        modifier = Modifier.size(32.dp),
                    ) {
                        Icon(Glyphs.ChevronRight, "More", Modifier.size(16.dp), tint = Text.copy(alpha = 0.8f))
                    }
                }
            }
        }
    }
}
