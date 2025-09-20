package git.yannynz.organizadorproducao;

import static org.assertj.core.api.Assertions.assertThat;
import static org.mockito.ArgumentMatchers.any;
import static org.mockito.ArgumentMatchers.eq;
import static org.mockito.Mockito.never;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.verifyNoMoreInteractions;
import static org.mockito.Mockito.when;

import com.fasterxml.jackson.databind.ObjectMapper;
import git.yannynz.organizadorproducao.model.Order;
import git.yannynz.organizadorproducao.repository.OrderRepository;
import git.yannynz.organizadorproducao.service.DobrasFileService;
import java.time.ZoneId;
import java.time.ZonedDateTime;
import java.util.Optional;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.extension.ExtendWith;
import org.mockito.Mock;
import org.mockito.junit.jupiter.MockitoExtension;
import org.springframework.messaging.simp.SimpMessagingTemplate;
import org.springframework.test.util.ReflectionTestUtils;

@ExtendWith(MockitoExtension.class)
class DobrasFileServiceTest {

    @Mock
    private OrderRepository orderRepository;

    @Mock
    private SimpMessagingTemplate messagingTemplate;

    private DobrasFileService service;

    @BeforeEach
    void setUp() {
        service = new DobrasFileService(new ObjectMapper(), orderRepository, messagingTemplate);
    }

    @Test
    void extractOrderNumber_CapturaNumeroMesmoComRuido() {
        Optional<String> nr = invokeExtract("NR 987654 RETRABALHO.m.DXF");

        assertThat(nr).contains("987654");
    }

    @Test
    void extractOrderNumber_AceitaSufixoDxfFcd() {
        Optional<String> nr = invokeExtract("NR999991 AJUSTE.DXF.FCD");

        assertThat(nr).contains("999991");
    }

    @Test
    void extractOrderNumber_RecusaFormatoInvalido() {
        assertThat(invokeExtract("NR 123456.txt")).isEmpty();
        assertThat(invokeExtract("arquivo qualquer.m.DXF")).isEmpty();
    }

    @Test
    void handleDobrasQueue_AtualizaPedidoQuandoValido() {
        Order order = new Order();
        order.setNr("123456");
        order.setStatus(5);

        when(orderRepository.findByNr("123456")).thenReturn(Optional.of(order));
        when(orderRepository.save(any(Order.class))).thenAnswer(invocation -> invocation.getArgument(0));

        service.handleDobrasQueue("{\"file_name\":\"NR 123456.m.DXF\"}");

        assertThat(order.getStatus()).isEqualTo(DobrasFileService.STATUS_TIRADA);
        assertThat(order.getDataTirada()).isNotNull();
        assertThat(order.getDataTirada().getZone()).isEqualTo(ZoneId.of("America/Sao_Paulo"));

        verify(orderRepository).save(order);
        verify(messagingTemplate).convertAndSend(eq("/topic/orders"), eq(order));
        verifyNoMoreInteractions(messagingTemplate);
    }

    @Test
    void updateOrderStatusToTirada_ReprocessamentoSobrescreveData() {
        Order order = new Order();
        order.setNr("777777");
        order.setStatus(DobrasFileService.STATUS_TIRADA);
        ZonedDateTime antigo = ZonedDateTime.now(ZoneId.of("America/Sao_Paulo")).minusHours(2);
        order.setDataTirada(antigo);

        when(orderRepository.findByNr("777777")).thenReturn(Optional.of(order));
        when(orderRepository.save(any(Order.class))).thenAnswer(invocation -> invocation.getArgument(0));

        invokeUpdate("777777");

        assertThat(order.getStatus()).isEqualTo(DobrasFileService.STATUS_TIRADA);
        assertThat(order.getDataTirada()).isNotNull();
        assertThat(order.getDataTirada()).isAfter(antigo);

        verify(orderRepository).save(order);
        verify(messagingTemplate).convertAndSend(eq("/topic/orders"), eq(order));
    }

    @Test
    void updateOrderStatusToTirada_PedidoInexistenteNaoDisparaEvento() {
        when(orderRepository.findByNr("111111")).thenReturn(Optional.empty());

        invokeUpdate("111111");

        verify(orderRepository, never()).save(any(Order.class));
        verifyNoMoreInteractions(messagingTemplate);
    }

    @SuppressWarnings("unchecked")
    private Optional<String> invokeExtract(String fileName) {
        Object result = ReflectionTestUtils.invokeMethod(service, "extractOrderNumber", fileName);
        return (Optional<String>) result;
    }

    private void invokeUpdate(String orderNumber) {
        ReflectionTestUtils.invokeMethod(service, "updateOrderStatusToTirada", orderNumber);
    }
}

