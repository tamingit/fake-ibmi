-- The demo's table, written as it would be for Db2 for i. The fake translates it for SQLite:
-- the library goes, as do FOR COLUMN and WITH DEFAULT's Db2 spelling.
create or replace table demolib.customer (
  id integer not null primary key,
  name for column cname char(20) not null with default,
  balance decimal(9, 2) not null with default,
  joined date not null with default current date
);

insert into demolib.customer (id, name, balance) values
  (1, 'Ada Lovelace', -300.00),
  (2, 'Grace Hopper', 1959.00),
  (3, 'Alan Turing', 1833.50),
  (4, 'Edsger Dijkstra', 1708.00),
  (5, 'Barbara Liskov', 1582.50),
  (6, 'Donald Knuth', 1457.00),
  (7, 'Margaret Hamilton', 1331.50),
  (8, 'Dennis Ritchie', 1206.00),
  (9, 'Ken Thompson', 1080.50),
  (10, 'Frances Allen', 955.00),
  (11, 'John Backus', 829.50),
  (12, 'Niklaus Wirth', 704.00),
  (13, 'Adele Goldberg', 578.50);
