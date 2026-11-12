**FREE
// CALL HELLO, or CALL HELLO ('your name').
dcl-pi *n;
  name char(20);
end-pi;

if %parms >= 1;
  dsply ('Hello, ' + %trim(name) + '!');
else;
  dsply 'Hello, world!';
endif;
return;
